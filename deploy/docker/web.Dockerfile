FROM node:22-alpine AS build
WORKDIR /web
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci
COPY src/Web/ .
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /web/.next/standalone ./
COPY --from=build /web/.next/static ./.next/static
USER node
EXPOSE 3000
CMD ["node", "server.js"]
