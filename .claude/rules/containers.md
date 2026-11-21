---
paths:
  - "**/compose.yml"
  - "**/compose.yaml"
  - "**/docker-compose.yml"
  - "**/docker-compose.yaml"
  - "**/docker-compose.yml.j2"
  - "**/Dockerfile"
---
# Container rules

- Pin explicit versions; avoid floating latest for stateful/platform services.
- Set restart behavior deliberately.
- Add meaningful health checks where supported.
- Persist stateful data explicitly.
- Use CPU/memory constraints where practical.
- Avoid host networking unless justified.
- Bind management/database ports to the WireGuard overlay address (`wg_overlay_ip`) or a private Docker network — never `0.0.0.0`; Docker's published ports bypass ufw.
- Avoid Docker socket mounts unless the security impact is explicitly accepted.
- Avoid privileged containers.
- Keep secrets out of Compose files.
