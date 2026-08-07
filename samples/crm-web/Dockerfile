# The CRM sample's web client.
#
# STATIC FILES AND NOTHING ELSE. The build produces a directory of assets; the runtime is a web
# server handing them out. There is no Node process in the shipped image — a client that needed
# one at run time would be a second application to patch, and this one has no server side.
#
# nginx-unprivileged rather than nginx: it listens on 8080 as uid 101 with no setuid binaries and
# no capability to bind a low port. The stock image starts as root to bind 80 and then drops —
# which works, and leaves a root-owned master process in every container for no benefit here.
#
# Build from the repository root:
#   docker build -f samples/crm-web/Dockerfile -t flowx-crm-web .

# ---------------------------------------------------------------------------------- build

FROM node:22-alpine AS build

WORKDIR /src

# `npm ci` and not `npm install`: the lockfile is the input, so the image is the versions that
# were tested rather than the versions that happened to be current when the image was built.
COPY samples/crm-web/package.json samples/crm-web/package-lock.json ./
RUN npm ci --no-audit --fund=false

COPY samples/crm-web/ ./

# Typecheck runs as part of `build` (tsc -b && vite build). A container that skipped it would
# ship a bundle the repository's own gate would have refused.
RUN npm run build

# ---------------------------------------------------------------------------------- runtime

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime

COPY --chown=nginx:nginx samples/crm-web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build --chown=nginx:nginx /src/dist /usr/share/nginx/html

USER nginx

EXPOSE 8080
