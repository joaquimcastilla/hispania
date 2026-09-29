import { describe, expect, it } from "vitest";
import { moduleNavigation, routeForPath, shellRoutes } from "./webModules";

describe("Web Shell module registry", () => {
  it("registers Media and Inventory through the same navigation and route catalog", () => {
    expect(moduleNavigation.map(route => route.area)).toEqual(["media", "media", "media", "media"]);
    expect(routeForPath("/erp/inventory")).toMatchObject({ section: "inventory", area: "erp" });
    expect(routeForPath("/")).toMatchObject({ section: "movies", area: "media" });
    expect(shellRoutes.some(route => route.area === "erp" && route.section === "inventory")).toBe(true);
  });
});
