# One multi-stage Dockerfile for the control plane.
# Build: docker build --build-arg SERVICE=AppHost src/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props PiAgent.slnx ./
COPY Platform/ ./Platform/
COPY Events/ ./Events/
COPY Orchestration/ ./Orchestration/
COPY GithubSync/ ./GithubSync/
COPY Publisher/ ./Publisher/
COPY RunWorker/ ./RunWorker/
COPY Hosts/ ./Hosts/
RUN dotnet publish "Hosts/$SERVICE/PiAgent.$SERVICE.csproj" -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG SERVICE
# curl is used by compose healthchecks (/healthz).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV APP_ENTRY=PiAgent.$SERVICE.dll
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_ENTRY"]
