#!/bin/sh
set -e

# Emit a runtime config.js for the SPA (read by window.APP_CONFIG in the web app).
# Only written if the SPA was bundled into wwwroot/.
if [ -d "wwwroot" ]; then
  cat > wwwroot/config.js <<EOF
window.APP_CONFIG = {
  API_URL: "${CORDIA_WEB_API_URL:-}"
};
EOF
fi

exec dotnet Cordia.Api.dll
