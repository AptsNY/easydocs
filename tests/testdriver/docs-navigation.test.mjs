import { describe, expect, it } from "vitest";
import { TestDriver } from "testdriverai/vitest/hooks";

// The production surface for easydocs is its published documentation site (the
// app itself is self-hosted, so there is no hosted instance to drive).
const DOCS_URL = "https://robertzu43.github.io/easydocs/";

describe("easydocs docs — navigation", () => {
  it('navigates from the home page to the "Getting started" guide', async (context) => {
    const testdriver = TestDriver(context);

    await testdriver.provision.chrome({ url: DOCS_URL });

    // Follow the left-hand nav to the Getting started guide.
    await testdriver
      .find('the "Getting started" link in the left navigation sidebar')
      .click();
    await testdriver.wait(2000);

    const result = await testdriver.assert(
      'the "Getting started" guide page is displayed, showing setup steps such as "Clone and configure" and "Bring it up"',
    );
    expect(result).toBeTruthy();
  });
});
