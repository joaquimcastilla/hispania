# HISPANIA Dev Launcher

Launcher WinForms exclusivo para desarrollo y pruebas. Inicia el backend ASP.NET Core y Vite, comprueba `/healthz` y la URL de Vite, abre el navegador y detiene únicamente los procesos que él mismo creó.

## Publicar

Desde la raíz del repositorio:

```powershell
dotnet publish tools/dev-launcher/Hispania.DevLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .artifacts/dev-launcher
```

Ejecutable generado: `.artifacts/dev-launcher/Hispania.DevLauncher.exe`.

El PC necesita Node.js/npm para Vite y el SDK/runtime de .NET compatible con el backend si se publica sin self-contained. La configuración local se guarda en `%LOCALAPPDATA%\Hispania\dev-launcher.json`; el token TMDB nunca se escribe allí. La base SQLite de pruebas por defecto es `%LOCALAPPDATA%\Hispania\visual-test.sqlite`.

La limpieza normal usa cierre controlado y `Process.Kill(entireProcessTree: true)` sólo sobre los procesos creados por el launcher. No mata procesos globales por nombre ni procesos de otros proyectos. La protección Job Object queda como mejora futura; por ello una terminación abrupta del launcher no tiene la misma garantía que el cierre normal.

El token Read Access de TMDB se lee de `tools/dev-launcher/tmdb-token.txt` dentro del repositorio seleccionado. El launcher crea el archivo vacío si falta; pega el token en una línea y reinicia los servidores. El archivo está excluido de Git y su contenido nunca aparece en la interfaz. El catálogo genera `.hispania-metadata-index.json` en la raíz Movies al iniciar y tras los escaneos; SQLite sigue siendo la fuente de verdad.
