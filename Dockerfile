# SPEC-016: multi-stage build — publish → chiseled runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/LLMRouter.Server -c Release -o /app/publish --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:20128 \
    LLMROUTER_DB_PATH=/data/llmrouter.db
VOLUME ["/data"]
EXPOSE 20128
ENTRYPOINT ["dotnet", "LLMRouter.Server.dll", "serve"]
