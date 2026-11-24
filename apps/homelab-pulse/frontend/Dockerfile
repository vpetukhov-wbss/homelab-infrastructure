FROM node:24.22.3-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:1.27.5-alpine
# Readable by uid 101 whatever umask the checkout used.
COPY --chmod=0644 nginx.conf /etc/nginx/nginx.conf
COPY --from=build /app/dist/pulse-dashboard/browser /usr/share/nginx/html
USER 101
EXPOSE 8080
