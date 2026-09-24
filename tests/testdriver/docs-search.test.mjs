import { describe, expect, it } from "vitest";
import { TestDriver } from "testdriverai/vitest/hooks";

// The production surface for easydocs is its published documentation site (the
// app itself is self-hosted, so there is no hosted instance to drive).
const DOCS_URL = "https://robertzu43.github.io/easydocs/";

describe("easydocs docs — search", () => {
  it("returns matching results from the docs search box", async (context) => {
    const testdriver = TestDriver(context);

    await testdriver.provision.chrome({ url: DOCS_URL });

    // Open the header search and query a term that appears across the guides.
    await testdriver
      .find(
        'the search input box in the top header (magnifying glass / "Search" field)',
      )
      .click();
    await testdriver.type("branch");
    await testdriver.wait(2000);

    const result = await testdriver.assert(
      'a search results dropdown is showing matching documents for the query "branch", including a result linking to the "Concepts" page',
    );
    expect(result).toBeTruthy();
  });
});
