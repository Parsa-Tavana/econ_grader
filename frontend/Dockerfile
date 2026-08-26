# ── Build stage ──────────────────────────────────────────────────────────────
FROM node:20-alpine AS build
WORKDIR /app

# Install first — layer cached until package manifests change.
COPY package.json package-lock.json ./
RUN npm ci

# Config + entry needed by Vite/Tailwind/TS.
COPY vite.config.ts tailwind.config.ts postcss.config.js tsconfig.json \
     tsconfig.app.json tsconfig.node.json index.html eslint.config.js ./
COPY public ./public
COPY src ./src

RUN npm run build

# ── Serve stage ──────────────────────────────────────────────────────────────
FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
