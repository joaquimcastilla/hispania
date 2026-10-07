import { defineConfig } from "@playwright/test";
export default defineConfig({ testDir: "./e2e", timeout: 120_000, workers: 1, retries: 0,
  outputDir: "../../.artifacts/phase20-browser", reporter: [["list"], ["json", { outputFile: "../../.artifacts/phase20-browser-results.json" }]],
  use: { browserName: "chromium", headless: true, trace: "retain-on-failure" },
  projects: [{ name: "modern" }, { name: "classic" }] });
