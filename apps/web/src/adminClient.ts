import { useCallback } from "react";
import { useAuth } from "./AuthContext";

const messages: Record<number, string> = {
  401: "La sesión ha caducado. Inicia sesión de nuevo.",
  403: "No tienes permiso para esta operación.",
  404: "El recurso no existe.",
  409: "El cambio entra en conflicto con el estado actual.",
};
export class AdminApiError extends Error {
  constructor(public readonly status: number) { super(messages[status] ?? (status >= 500 ? "Error del servidor. Vuelve a intentarlo." : "No se pudo completar la operación.")); }
}
export const adminError = (cause: unknown) => cause instanceof AdminApiError ? cause.message : "No se pudo conectar con el servidor.";
export function useAdminRequest() {
  const { request } = useAuth();
  return useCallback(async (path: string, init?: RequestInit) => {
    const response = await request(path, init);
    if (!response.ok) throw new AdminApiError(response.status);
    return response;
  }, [request]);
}
