import type { ComponentType } from "react";
import { AdminEventsPage, AdminJobsPage, AdminRolesPage, AdminUsersPage } from "./AuthorizationPages";
import { ObservabilityPage } from "./ObservabilityPage";
import { AdminAssetsPage, AdminModulesPage, AdminOverviewPage } from "./PlatformAdminPages";
import { AdminCapacityPage, AdminMaintenancePage, AdminReconciliationPage } from "./MaintenancePages";

export type AdminRoute = { path: string; label: string; permission: string; component: ComponentType };
// Static contributions can be composed here without changing the shell or App routing.
export const adminRoutes: readonly AdminRoute[] = [
  { path: "/admin", label: "Overview", permission: "platform.observability.read", component: AdminOverviewPage },
  { path: "/admin/users", label: "Usuarios", permission: "platform.users.read", component: AdminUsersPage },
  { path: "/admin/roles", label: "Roles", permission: "platform.roles.read", component: AdminRolesPage },
  { path: "/admin/events", label: "Eventos", permission: "platform.events.read", component: AdminEventsPage },
  { path: "/admin/jobs", label: "Jobs", permission: "platform.jobs.read", component: AdminJobsPage },
  { path: "/admin/maintenance", label: "Retención", permission: "platform.maintenance.read", component: AdminMaintenancePage },
  { path: "/admin/reconciliation", label: "Reconciliación", permission: "platform.maintenance.read", component: AdminReconciliationPage },
  { path: "/admin/capacity", label: "Capacidad", permission: "platform.capacity.read", component: AdminCapacityPage },
  { path: "/admin/observability", label: "Observabilidad", permission: "platform.observability.read", component: ObservabilityPage },
  { path: "/admin/assets", label: "Assets", permission: "platform.assets.write", component: AdminAssetsPage },
  { path: "/admin/modules", label: "Módulos", permission: "platform.admin", component: AdminModulesPage },
];
export const adminEntryPath = (hasPermission: (permission: string) => boolean) => adminRoutes.find(route => hasPermission(route.permission))?.path;
