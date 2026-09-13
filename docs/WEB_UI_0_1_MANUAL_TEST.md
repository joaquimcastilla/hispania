# Web UI 0.1 — prueba manual

Esta prueba valida Direct Play local para un MP4 con vídeo H.264 y audio AAC. No hay transcoding ni fallback de formato en este milestone.

## Preparación

1. Crea una carpeta para la biblioteca y coloca en ella una película MP4 H.264/AAC.
2. Identifica la ruta absoluta de `ffprobe`.
3. Configura el host mediante variables de entorno PowerShell:

   ```powershell
   $env:Library__Roots__0__Key = "movies"
   $env:Library__Roots__0__Path = "C:\media\movies"
   $env:Ffprobe__ExecutablePath = "C:\tools\ffmpeg\bin\ffprobe.exe"
   ```

4. Instala y compila la Web UI:

   ```powershell
   Set-Location apps/web
   npm ci
   npm run build
   Set-Location ../..
   ```

5. Arranca el servidor desde la raíz del repositorio:

   ```powershell
   $env:ASPNETCORE_URLS = "http://localhost:5000"
   dotnet run --project apps/server/Hispania.Server.csproj
   ```

6. Abre la URL indicada por ASP.NET Core (por ejemplo, `http://localhost:5000`).

## Comprobación

1. Confirma que aparece la película en **Movies**.
2. Selecciónala; debe aparecer el reproductor nativo.
3. Pulsa Play y confirma vídeo y audio.
4. Mueve la barra de progreso a otro punto.
5. Confirma que la reproducción continúa desde la nueva posición. El navegador solicitará los byte ranges directamente a `/api/v1/media/{id}/stream`.

## Desarrollo con Vite

Con el servidor ASP.NET configurado y activo, ejecuta lo siguiente en `apps/web`:

```powershell
$env:HISPANIA_API_URL = "http://localhost:5000"
npm run dev
```

Vite sirve la interfaz y reenvía únicamente `/api` al valor de `HISPANIA_API_URL` (por defecto `http://localhost:5000`); las peticiones de React siguen usando rutas relativas. No es necesario cambiar código para utilizar otro puerto.

## Publicación

`npm run build` genera `apps/web/dist`. Durante `dotnet publish`, `Hispania.Server.csproj` copia esos assets al `wwwroot` publicado. En producción el servidor entrega la API y la SPA bajo el mismo origen, sin configurar CORS.
