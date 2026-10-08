import { test, expect } from "@playwright/test";
import { spawn, execFileSync, type ChildProcess } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, cpSync, copyFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { createServer } from "node:net";
import { randomBytes } from "node:crypto";

const repo = resolve(import.meta.dirname, "../../..");
const route = "/extensions/test.package.web/dashboard";
const operation = "/api/v1/web/contributions/test.package.web/dashboard/operations/platform.health.refresh";
test("real signed package lifecycle, Session RBAC, safe rendering and CSP", async ({ page, request }, info) => {
  const root = mkdtempSync(join(tmpdir(), "hispania-browser-"));
  mkdirSync(join(root, "library"));
  execFileSync("dotnet", [join(repo, "tests/web-contribution-fixtures/bin/Debug/net10.0/Hispania.Web.Fixtures.dll"), root]);
  const webroot = join(root, "wwwroot");
  cpSync(join(repo, "apps/web/dist", info.project.name), webroot, { recursive: true });
  if (info.project.name === "classic") copyFileSync(join(webroot, "index.classic.html"), join(webroot, "index.html"));
  const keys = JSON.parse(readFileSync(join(root, "keys.json"), "utf8"));
  const token = randomBytes(24).toString("hex");
  const port = await new Promise<number>(done => { const server = createServer(); server.listen(0, "127.0.0.1", () => { const address = server.address(); const port = typeof address === "object" && address ? address.port : 0; server.close(() => done(port)); }); });
  const base = `http://127.0.0.1:${port}`;
  const serverDir = join(repo, "apps/server/bin/Debug/net10.0");
  const args = [join(serverDir, "Hispania.Server.dll"), `--urls=${base}`, `--contentRoot=${serverDir}`,
    `--webroot=${webroot}`, `--Web:Build=${info.project.name}`,
    `--Persistence:Sqlite:Path=${join(root, "state", "media.sqlite")}`, `--Inventory:Sqlite:Path=${join(root, "state", "inventory.sqlite")}`,
    `--Platform:DataPath=${join(root, "state", "platform")}`, `--Backup:ManagedRoot=${join(root, "state")}`, `--DistributedWorkers:StatePath=${join(root, "state", "coordinator.sqlite")}`,
    `--DistributedWorkers:TransferPath=${join(root, "state", "transfers")}`, `--Library:Roots:0:Key=empty`, `--Library:Roots:0:Path=${join(root, "library")}`,
    "--Ffprobe:ExecutablePath=ffprobe", "--Ffmpeg:ExecutablePath=ffmpeg", `--Playback:TranscodeCachePath=${join(root, "state", "cache")}`,
    `--PlaybackExperiments:StatePath=${join(root, "state", "playback.json")}`, "--Modules:ArchitectureDemonstration=hispania.architecture-test.a",
    `--Modules:AdminToken=${token}`, `--Identity:Bootstrap:Token=${token}`, `--Testing:ShutdownToken=${token}`,
    `--Modules:ExtensionsPath=${join(root, "state", "extensions")}`, "--Modules:TrustedExtensionIds:0=test.package.web", "--Modules:RequirePublisherSignature=true",
    `--Modules:TrustedPublisherKeys:TEST-ONLY-web=${keys["TEST-ONLY-web"]}`];
  let child: ChildProcess | undefined; let logs = "";
  let probingCsp = false;
  const unexpectedCsp: string[] = [];
  page.on("console", message => { if (!probingCsp && /content security policy|violates.*directive/i.test(message.text())) unexpectedCsp.push(message.text()); });
  try {
    child = spawn("dotnet", args, { cwd: serverDir, windowsHide: true, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Production" } });
    child.stdout?.on("data", data => { logs += String(data); }); child.stderr?.on("data", data => { logs += String(data); });
    await expect.poll(async () => { if (child?.exitCode !== null) throw new Error(logs); return request.get(base + "/readyz").then(r => r.status()).catch(() => 0); }, { timeout: 45_000 }).toBe(200);
    const password = "TEST-ONLY-Phase20-Password!123";
    const create = await request.post(base + "/api/identity/users", { headers: { Authorization: `Bearer ${token}` }, data: { username: "webadmin", displayName: "Web admin", password } });
    expect(create.ok()).toBeTruthy();
    const login = await request.post(base + "/api/identity/login", { data: { username: "webadmin", password } });
    expect(login.headers()["cache-control"]).toBe("no-store");
    expect(login.headers()["x-content-type-options"]).toBe("nosniff");
    expect(login.headers()["x-frame-options"]).toBe("DENY");
    expect(login.headers()["referrer-policy"]).toBe("no-referrer");
    expect(login.headers()["content-security-policy"]).toContain("frame-ancestors 'none'");
    const session = (await login.json()).sessionToken;
    const headers = { Authorization: `Session ${session}` };
    const me = await request.get(base + "/api/identity/me", { headers }); const userId = (await me.json()).user.id.value;
    expect((await request.post(base + "/api/authorization/bootstrap-admin", { headers: { Authorization: `Bootstrap ${token}` }, data: { userId } })).ok()).toBeTruthy();
    // Extension permissions are deliberately outside platform.admin. Grant explicitly through Phase13.
    const roleResult = await request.post(base + "/api/authorization/roles", { headers, data: { name: "Web viewer", description: "TEST ONLY explicit grant" } });
    const role = await roleResult.json(); const roleId = role.id.value;
    expect((await request.put(base + `/api/authorization/roles/${roleId}/permissions/extensions.test.package.web.view`, { headers })).ok()).toBeTruthy();
    expect((await request.post(base + `/api/authorization/users/${userId}/roles/${roleId}`, { headers })).ok()).toBeTruthy();
    await page.goto(base + "/login?returnTo=" + encodeURIComponent(route));
    await page.getByLabel("Usuario").fill("webadmin"); await page.getByLabel("Contraseña").fill(password); await page.getByRole("button", { name: "Entrar" }).click();
    await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    async function upload(version: string) { return request.post(base + "/api/platform/admin/modules/packages", { headers: { ...headers, "Content-Type": "application/octet-stream" }, data: readFileSync(join(root, version + ".hispania")) }); }
    async function lifecycle(operation: string) { return request.post(base + `/api/platform/admin/modules/packages/test.package.web/${operation}`, { headers }); }
    expect((await upload("1.0.0")).ok()).toBeTruthy();
    await page.reload(); await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    expect((await lifecycle("enable")).status()).toBe(204);
    await page.reload(); await expect(page.getByRole("heading", { name: "Version One", exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "Version One", exact: true })).toBeVisible();
    await expect(page.getByText("<script>alert(1)</script>", { exact: true })).toBeVisible();
    await expect(page.getByRole("table")).toBeVisible();
    await expect(page.getByRole("cell", { name: '<img src=x onerror="window.__xss=1">', exact: true })).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
    expect((await request.get(base + operation, { headers })).status()).toBe(405);
    expect((await request.post(base + operation.replace("platform.health.refresh", "platform.health"), { headers })).status()).toBe(405);
    expect((await request.post(base + operation.replace("platform.health.refresh", "arbitrary.operation"), { headers })).status()).toBe(404);
    expect((await request.post(base + operation, { headers: { Authorization: `Bearer ${token}` } })).status()).toBe(401);
    expect((await request.post(base + operation, { headers: { Authorization: "Session " + "0".repeat(64) } })).status()).toBe(401);
    const asset = page.getByRole("link", { name: "Version One", exact: true }).locator("img");
    await expect(asset).toBeVisible(); const oldAsset = await asset.getAttribute("src");
    const png = await request.get(base + oldAsset); expect(png.status()).toBe(200);
    expect(png.headers()["content-type"]).toContain("image/png"); expect(png.headers()["x-content-type-options"]).toBe("nosniff");
    expect(png.headers()["cache-control"]).toBe("no-store");
    for (const suffix of ["js", "css", "html", "svg", "PNG"]) expect((await request.get(base + oldAsset!.replace("icon.png", "icon." + suffix))).status()).toBe(404);
    expect((await request.get(base + oldAsset!.replace("icon.png", "%2e%2e%2ficon.png"))).status()).toBe(404);
    await page.getByRole("button", { name: "Comprobar estado" }).click(); await expect(page.getByText("Acción aceptada")).toBeVisible();
    const response = await request.get(base + route); const csp = response.headers()["content-security-policy"];
    expect(csp).toContain("script-src 'self'"); expect(csp).not.toContain("unsafe-eval");
    expect(unexpectedCsp).toEqual([]);
    probingCsp = true;
    // DevTools evaluate itself bypasses unsafe-eval checks; run the probe as an actual origin script.
    await page.route("**/assets/csp-probe.js", route => route.fulfill({ contentType: "text/javascript", body:
      "window.cspProbe = {eval: false, inline: false}; try { eval('1+1'); } catch { window.cspProbe.eval = true; } window.injected=false; const s=document.createElement('script'); s.textContent='window.injected=true'; document.body.append(s); window.cspProbe.inline=!window.injected;" }));
    await page.evaluate(() => { const script = document.createElement("script"); script.src = "/assets/csp-probe.js"; document.body.append(script); });
    await expect.poll(() => page.evaluate(() => (window as unknown as { cspProbe: unknown }).cspProbe)).toEqual({ inline: true, eval: true });
    probingCsp = false;
    // New UI reaches staging but unhealthy backend must restore Version One.
    expect((await upload("1.1.0")).status()).toBe(409); await page.reload(); await expect(page.getByRole("heading", { name: "Version One", exact: true })).toBeVisible();
    expect((await upload("2.0.0")).ok()).toBeTruthy(); await page.reload(); await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    expect((await request.get(base + oldAsset)).status()).toBe(404);
    const newAsset = await page.getByRole("link", { name: "Version Two", exact: true }).locator("img").getAttribute("src");
    const newPng = await request.get(base + newAsset);
    expect(newPng.status()).toBe(200);
    expect((await newPng.body()).equals(await png.body())).toBe(false);
    expect((await upload("3.0.0")).status()).toBe(409); await page.reload(); await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    expect((await (await request.get(base + newAsset)).body()).equals(await newPng.body())).toBe(true);
    expect((await lifecycle("disable")).status()).toBe(204);
    expect((await request.post(base + operation, { headers })).status()).toBe(404);
    await page.reload(); await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    expect((await lifecycle("enable")).status()).toBe(204); await page.reload(); await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    // Actual Phase17 offline bundle: stop Server, destroy managed state, restore, restart, hard refresh.
    await request.post(base + "/_test/shutdown", { headers: { Authorization: `Bearer ${token}` } });
    await expect.poll(() => child?.exitCode, { timeout: 20_000 }).toBe(0);
    execFileSync("dotnet", [join(serverDir, "Hispania.Server.dll"), "--backup-create", join(root, "state"), join(root, "bundle")], { cwd: serverDir });
    const stateRoot = resolve(root, "state");
    if (stateRoot !== join(root, "state")) throw new Error("Unexpected fixture deletion target");
    rmSync(stateRoot, { recursive: true });
    execFileSync("dotnet", [join(serverDir, "Hispania.Server.dll"), "--backup-restore", join(root, "bundle"), stateRoot], { cwd: serverDir });
    child = spawn("dotnet", args, { cwd: serverDir, windowsHide: true, env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Production" } });
    child.stdout?.on("data", data => { logs += String(data); }); child.stderr?.on("data", data => { logs += String(data); });
    await expect.poll(() => request.get(base + "/readyz").then(r => r.status()).catch(() => 0), { timeout: 45_000 }).toBe(200);
    await page.reload(); await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    const second = await request.post(base + "/api/identity/users", { headers, data: { username: "webdenied", displayName: "Denied", password } }); expect(second.ok()).toBeTruthy();
    const secondLogin = await request.post(base + "/api/identity/login", { data: { username: "webdenied", password } }); const deniedHeaders = { Authorization: `Session ${(await secondLogin.json()).sessionToken}` };
    expect((await request.post(base + operation, { headers: deniedHeaders })).status()).toBe(403);
    expect((await (await request.get(base + "/api/v1/web/contributions", { headers: deniedHeaders })).json()).contributions).toHaveLength(0);
    expect((await request.post(base + operation)).status()).toBe(401);
    // A second browser session has neither visible navigation nor direct-route permission.
    await page.evaluate(() => sessionStorage.clear()); await page.goto(base + "/login?returnTo=" + encodeURIComponent(route));
    await page.getByLabel("Usuario").fill("webdenied"); await page.getByLabel("Contraseña").fill(password); await page.getByRole("button", { name: "Entrar" }).click();
    await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Version Two", exact: true })).toHaveCount(0);
    // Partial grant: visible page, denied operation, disabled action in the actual browser.
    const deniedMe = await request.get(base + "/api/identity/me", { headers: deniedHeaders }); const deniedId = (await deniedMe.json()).user.id.value;
    expect((await request.post(base + `/api/authorization/users/${deniedId}/roles/${roleId}`, { headers })).ok()).toBeTruthy();
    await page.reload(); await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    await expect(page.getByRole("button", { name: "Comprobar estado" })).toBeDisabled();
    await expect(page.getByText("Acceso denegado", { exact: true })).toBeVisible();
    expect((await request.post(base + operation, { headers: deniedHeaders })).status()).toBe(403);
    await page.evaluate(token => sessionStorage.setItem("hispania.identity.session", token), session); await page.goto(base + route);
    await expect(page.getByRole("heading", { name: "Version Two", exact: true })).toBeVisible();
    expect((await lifecycle("uninstall")).status()).toBe(204); await page.reload(); await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    expect((await upload("2.0.0")).ok()).toBeTruthy(); expect((await lifecycle("enable")).status()).toBe(204);
    for (const [version, title] of [["10.0.0", "<script>window.__xss=1</script>"], ["11.0.0", '<img src=x onerror="window.__xss=1">']]) {
      expect((await upload(version)).ok()).toBeTruthy(); await page.reload();
      await expect(page.getByRole("heading", { name: title, exact: true })).toBeVisible();
      await expect(page.getByRole("link", { name: title, exact: true })).toBeVisible();
      expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
      expect(unexpectedCsp).toEqual([]);
    }
    expect((await request.delete(base + "/internal/admin/modules/publishers/TEST-ONLY-web", { headers: { Authorization: `Bearer ${token}` } })).ok()).toBeTruthy();
    await page.reload(); await expect(page.getByRole("heading", { name: "Página no encontrada" })).toBeVisible();
    expect((await request.post(base + operation, { headers })).status()).toBe(404);
    await request.post(base + "/api/identity/logout", { headers }); await page.reload();
    await expect(page.getByRole("heading", { name: "Iniciar sesión" })).toBeVisible();
    await request.post(base + "/_test/shutdown", { headers: { Authorization: `Bearer ${token}` } });
    await new Promise<void>((done, reject) => { const timer = setTimeout(() => reject(new Error("Server shutdown timeout")), 20_000); child!.once("exit", code => { clearTimeout(timer); code === 0 ? done() : reject(new Error(`exit ${code}`)); }); });
  } finally {
    if (child && child.exitCode === null) child.kill();
    writeFileSync(join(root, "server.log"), logs); await info.attach("server-log", { path: join(root, "server.log"), contentType: "text/plain" });
  }
});
