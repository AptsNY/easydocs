import { describe, expect, it } from "vitest";
import { TestDriver } from "testdriverai/vitest/hooks";

// easydocs is a self-hostable app (installed with `docker compose up`), so there
// is no public/hosted "production" instance of the app itself. The one publicly
// deployed production surface is the documentation site published from this repo
// via GitHub Pages, so these sample tests target that live site.
const DOCS_URL = "https://robertzu43.github.io/easydocs/";

describe("easydocs docs — home page", () => {
  it("loads the production docs home page with its heading and tagline", async (context) => {
    const testdriver = TestDriver(context);

    await testdriver.provision.chrome({ url: DOCS_URL });

    const result = await testdriver.assert(
      'the easydocs documentation home page is visible with the heading "easydocs" and the tagline about Git-style history for .docx',
    );
    expect(result).toBeTruthy();
  });
});
