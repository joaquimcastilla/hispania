# HISPANIA Web UI

The application produces two independent static build entrypoints that use the same public HTTP API:

- `npm run build:modern` -> `dist/modern`
- `npm run build:classic` -> `dist/classic`
- `npm run build` -> both outputs

Select one when starting the server with `Web:Build` set to `modern` or `classic`. The default is `modern`. The server process must restart to change builds. Both currently share React screens and navigation; only the entrypoint and theme layer are independent.
