FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# Copy pre-published API output (run `bun nx run api:publish` first)
COPY dist/ .

# Copy pre-built React SPA into wwwroot (optional single-image deploy)
COPY web/ wwwroot/

# Generate runtime config from CORDIA_WEB_* env vars, then start the API.
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh

EXPOSE 8080

ENTRYPOINT ["./docker-entrypoint.sh"]
