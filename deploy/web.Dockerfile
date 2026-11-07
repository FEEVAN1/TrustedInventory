# Builds the PWA with the locked npm dependency set and serves it from an unprivileged nginx.
FROM node:24.11.0-alpine AS build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29.3-alpine
COPY deploy/web-nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 8080
