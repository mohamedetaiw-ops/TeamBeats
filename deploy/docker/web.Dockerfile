FROM node:22-alpine AS build
WORKDIR /web
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci
COPY src/Web/ .
# next.config.ts reads the API origin at build time for the /api and /dev rewrites; deployments pass their private API address.
ARG TEAMBEATS_API_ORIGIN=http://localhost:5080
ENV TEAMBEATS_API_ORIGIN=$TEAMBEATS_API_ORIGIN
# The release named in frontend error reports (spec 021 US9); deployments pass the commit.
ARG TEAMBEATS_RELEASE=dev
ENV TEAMBEATS_RELEASE=$TEAMBEATS_RELEASE
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /web/.next/standalone ./
COPY --from=build /web/.next/static ./.next/static
# Standalone output leaves out public/, so the design system illustrations are copied next to the server.
COPY --from=build /web/public ./public
USER node
EXPOSE 3000
CMD ["node", "server.js"]
