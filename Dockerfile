# syntax=docker/dockerfile:1.7

# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props copilotd.slnx ./
COPY src/copilotd/copilotd.csproj src/copilotd/
RUN dotnet restore src/copilotd/copilotd.csproj

COPY src/ src/
# The container runs the framework-dependent build; AOT publishing is kept for
# the native installers and is not needed (or cheap) inside the image.
RUN dotnet publish src/copilotd/copilotd.csproj -c Release -o /app \
        -p:PublishAot=false --no-restore

# ── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

ARG COPILOT_CLI_PACKAGE=@github/copilot
ARG COPILOT_CLI_VERSION=latest

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    COPILOTD_DISABLE_SELF_UPDATES=1 \
    HOME=/home/copilotd \
    COPILOTD_HOME=/data/copilotd \
    COPILOTD_REPO_HOME=/repos

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash ca-certificates curl dumb-init git gnupg procps ripgrep \
    && install -d -m 0755 /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs gh \
    && npm install -g "${COPILOT_CLI_PACKAGE}@${COPILOT_CLI_VERSION}" \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*

# The base image ships an "ubuntu" user at UID 1000; replace it so the container
# user matches the common Linux host UID and bind-mounted ~/.copilot stays writable.
RUN userdel -r ubuntu 2>/dev/null || true \
    && useradd --create-home --home-dir /home/copilotd --uid 1000 --shell /bin/bash copilotd \
    && mkdir -p /opt/copilotd /data/copilotd /repos \
    && chown -R copilotd:copilotd /data /repos /opt/copilotd /home/copilotd

COPY --from=build --chown=copilotd:copilotd /app /opt/copilotd
COPY docker/entrypoint.sh /usr/local/bin/copilotd-entrypoint
COPY docker/copilotd.sh /usr/local/bin/copilotd
RUN chmod 0755 /usr/local/bin/copilotd-entrypoint /usr/local/bin/copilotd

USER copilotd
WORKDIR /home/copilotd

# /repos is the mounted host repo root (repo_home). Worktrees are created in
# sibling <repo>_sessions folders, so mount the whole root, not a single repo.
VOLUME ["/data/copilotd", "/repos"]

ENTRYPOINT ["/usr/bin/dumb-init", "--", "/usr/local/bin/copilotd-entrypoint"]
CMD ["run", "--interval", "60"]
