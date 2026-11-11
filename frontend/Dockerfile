FROM node:24.13.0-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-bin-links --no-audit --no-fund
COPY . .
RUN npm run build
FROM nginx:1.28.1-alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist/browser/browser /usr/share/nginx/html
ENV API_UPSTREAM=http://backend:5080
EXPOSE 80
