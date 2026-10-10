export { adminRoutes, adminEntryPath, type AdminRoute } from "./adminRoutes";
export type ShellSection = "home" | "movies" | "series" | "collections" | "music" | "dashboard" | "inventory";
export type ShellRoute = { section: ShellSection; path: string; label: string; area: "media" | "erp" | "platform" };

/** Composition registry for module navigation and top-level route ownership. */
export const shellRoutes: readonly ShellRoute[] = [
  { section: "movies", path: "/", label: "Películas", area: "media" },
  { section: "series", path: "/series", label: "Series", area: "media" },
  { section: "collections", path: "/collections", label: "Colecciones", area: "media" },
  { section: "music", path: "/music", label: "Música", area: "media" },
  { section: "inventory", path: "/erp/inventory", label: "Inventario", area: "erp" },
];

export function routeForPath(path: string, contributions?: readonly import("./DynamicWeb").WebContribution[]): (ShellRoute & { contribution?: import("./DynamicWeb").WebContribution }) | undefined {
  const builtIn = shellRoutes.find(route => route.path === path);
  if (builtIn) return builtIn;
  const contribution = contributions?.find(item => item.page.route === path);
  return contribution ? { section: "home", path, label: contribution.page.title, area: "platform", contribution } : undefined;
}

export const moduleNavigation = shellRoutes.filter(route => route.area === "media");
