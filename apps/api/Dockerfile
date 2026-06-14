FROM mcr.microsoft.com/dotnet/aspnet:10.0

# SPA path prefix when fronted by a CDN/gateway. Must match the Vite `base`
# (VITE_BASE_PATH) used for the web build. Default "/" for root hosting.
ARG WEB_BASE_PATH=/

WORKDIR /app

# Copy pre-published API output (run `bun nx run api:publish` first)
COPY dist/ .

# Copy pre-built React SPA into wwwroot (optional single-image deploy)
COPY web/ wwwroot/

# Inject the runtime-config script into the SPA shell so the browser loads
# ${WEB_BASE_PATH}config.js (written by docker-entrypoint.sh at startup), which
# sets window.APP_CONFIG.API_URL consumed by the web app's fetch client.
RUN if [ -f wwwroot/index.html ]; then \
      sed -i "s|</head>|<script src=\"${WEB_BASE_PATH}config.js\"></script></head>|" wwwroot/index.html; \
    fi

# Generate runtime config from CORDIA_WEB_* env vars, then start the API.
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

EXPOSE 8080

ENTRYPOINT ["./docker-entrypoint.sh"]
