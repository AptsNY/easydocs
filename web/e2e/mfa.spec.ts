import { createHmac } from 'node:crypto'
import { test, expect, register, signIn } from './fixtures'

// Issue #10 in the browser: enable TOTP from Settings, then a fresh sign-in demands the code and a
// correct one finishes it. The code is computed here with node:crypto — same RFC 6238 the app and
// every authenticator implement.

function base32Decode(s: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
  let bits = 0
  let buffer = 0
  const out: number[] = []
  for (const c of s.replace(/=+$/, '').toUpperCase()) {
    const v = alphabet.indexOf(c)
    if (v < 0) throw new Error(`not base32: ${c}`)
    buffer = (buffer << 5) | v
    bits += 5
    if (bits >= 8) {
      out.push((buffer >> (bits - 8)) & 0xff)
      bits -= 8
    }
  }
  return Buffer.from(out)
}

function totp(secret: string, at = Date.now()): string {
  const counter = Math.floor(at / 1000 / 30)
  const msg = Buffer.alloc(8)
  msg.writeBigInt64BE(BigInt(counter))
  const h = createHmac('sha1', base32Decode(secret)).update(msg).digest()
  const o = h[h.length - 1] & 0x0f
  const code = (((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]) % 1_000_000
  return code.toString().padStart(6, '0')
}

test('enable TOTP in Settings, then sign in with a code', async ({ page, request }) => {
  const account = await register(request)
  await signIn(page, account)

  await page.goto('/settings')
  await page.getByTestId('mfa-begin').click()
  const secret = (await page.getByTestId('mfa-secret').textContent())!.trim()
  await page.getByTestId('mfa-enable').getByRole('textbox').fill(totp(secret))
  await page.getByTestId('mfa-enable').getByRole('button', { name: 'Turn on' }).click()

  // Recovery codes render exactly once, right here.
  await expect(page.getByTestId('mfa-recovery-codes')).toBeVisible()
  await expect(page.getByTestId('mfa-section')).toContainText('On — 10 recovery codes left')

  // Fresh sign-in now needs the second factor.
  await page.getByRole('button', { name: 'Sign out' }).click()
  await page.getByLabel('Email').fill(account.email)
  await page.getByLabel('Password').fill(account.password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()

  await expect(page.getByTestId('mfa-form')).toBeVisible()
  await page.getByTestId('mfa-form').getByRole('textbox').fill(totp(secret))
  await page.getByTestId('mfa-form').getByRole('button', { name: 'Verify' }).click()
  // Signed in again — where exactly depends on which route the auth guard stashed as the return
  // destination, so assert the session, not a particular page.
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
})
