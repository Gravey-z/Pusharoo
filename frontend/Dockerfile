FROM node:24.16.0-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27.5-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY runtime-config.json.template /usr/share/nginx/runtime-config.json.template
COPY runtime-config-entrypoint.sh /docker-entrypoint.d/15-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/15-runtime-config.sh
COPY --from=build /app/dist/frontend/browser /usr/share/nginx/html
EXPOSE 80
