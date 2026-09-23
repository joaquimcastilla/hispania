export type MetadataValue = string | number | boolean | null;
export type MetadataSource = { origin: string; providerCode: string | null; observedAt: string; value: MetadataValue };
export type MetadataField = { fieldCode: string; effective: MetadataValue; manual: MetadataValue; locked: boolean; sources: MetadataSource[] };
export type MetadataCollection = { collectionCode: string; effective: string[]; manual: string[]; locked: boolean; sources: { origin: string; providerCode: string | null; observedAt: string; values: string[] }[] };
export type MetadataEditModel = { mediaId: string; workId: string; settings: { preferredMetadataLanguage: string | null; metadataCountryRegion: string | null; lockItemMetadata: boolean }; locks: { fieldCode: string; locked: boolean }[]; fields: MetadataField[]; collections: MetadataCollection[]; people: { providerId: string; name: string; profileArtworkLocator?: string | null; providerCode?: string | null; internalPersonId?: string | null }[]; credits: { personId: string; workId: string; creditType: string; character: string | null; job: string | null; ordinal: number | null; origin: string; providerCode: string | null; observedAt: string }[] };
export type ApiError = { code?: string; message?: string };
export type PersonMetadata = { personId: string; providerCode?: string | null; providerId: string; name: string; profileArtworkLocator?: string | null; biography?: string | null; birthday?: string | null; deathday?: string | null; placeOfBirth?: string | null; profileObservedAt?: string | null; filmography: { mediaId: string; displayName: string; title: string; releaseDate?: string | null; posterUrl?: string | null }[] };
async function request<T>(url: string, init?: RequestInit): Promise<T> { const response = await fetch(url, { ...init, headers: { "content-type": "application/json", ...(init?.headers ?? {}) } }); if (!response.ok) { let error: ApiError = {}; try { error = await response.json() as ApiError; } catch { /* non-json */ } throw Object.assign(new Error(error.message ?? `HTTP ${response.status}`), { status: response.status, code: error.code }); } return await response.json() as T; }
const base = (id: string) => `/api/v1/media/${encodeURIComponent(id)}/metadata`;
export const metadataClient = {
  get: (id: string) => request<MetadataEditModel>(`${base(id)}/edit`),
  getPerson: (id: string) => request<PersonMetadata>(`/api/v1/people/${encodeURIComponent(id)}`),
  settings: (id: string, value: MetadataEditModel["settings"]) => request<MetadataEditModel>(`${base(id)}/settings`, { method: "PUT", body: JSON.stringify(value) }),
  lock: (id: string, fieldCode: string, locked: boolean) => request<MetadataEditModel>(`${base(id)}/locks/${encodeURIComponent(fieldCode)}`, { method: "PUT", body: JSON.stringify({ locked }) }),
  scalar: (id: string, fieldCode: string, value: MetadataValue) => request<MetadataEditModel>(`${base(id)}/scalars/${encodeURIComponent(fieldCode)}/manual`, { method: "PUT", body: JSON.stringify({ value }) }),
  clearScalar: (id: string, fieldCode: string) => request<MetadataEditModel>(`${base(id)}/scalars/${encodeURIComponent(fieldCode)}/manual`, { method: "DELETE" }),
  collection: (id: string, code: string, values: string[]) => request<MetadataEditModel>(`${base(id)}/${code}/manual`, { method: "PUT", body: JSON.stringify({ values }) }),
  clearCollection: (id: string, code: string) => request<MetadataEditModel>(`${base(id)}/${code}/manual`, { method: "DELETE" }),
};
