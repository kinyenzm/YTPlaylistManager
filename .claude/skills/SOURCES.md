# Procedencia de las skills

Copias sin modificar de repositorios públicos. Para actualizar una skill, vuelve a copiar la carpeta
de origen sobre la de aquí y actualiza el commit en esta tabla.

| Skill | Repositorio | Carpeta de origen | Commit | Licencia |
|---|---|---|---|---|
| `vercel-react-best-practices` | [vercel-labs/agent-skills](https://github.com/vercel-labs/agent-skills) | `skills/react-best-practices` | `063bee9` (2026-08-28) | MIT |
| `web-design-guidelines` | [vercel-labs/agent-skills](https://github.com/vercel-labs/agent-skills) | `skills/web-design-guidelines` | `063bee9` (2026-08-28) | MIT |
| `design-taste-frontend` | [Leonxlnx/taste-skill](https://github.com/Leonxlnx/taste-skill) | `skills/taste-skill` | `6010f91` (2026-10-20, v2.1.4) | MIT |
| `high-end-visual-design` | [Leonxlnx/taste-skill-classics](https://github.com/Leonxlnx/taste-skill-classics) | `skills/soft-skill` | `5e08c93` (2026-10-19) | MIT |
| `minimalist-ui` | [Leonxlnx/taste-skill-classics](https://github.com/Leonxlnx/taste-skill-classics) | `skills/minimalist-skill` | `5e08c93` (2026-10-19) | MIT |

Notas:

- `web-design-guidelines` no trae las reglas: en cada uso descarga
  `https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md`
  y las aplica. Su contenido puede cambiar sin que cambie este repo.
- `high-end-visual-design` y `minimalist-ui` tienen `disable-model-invocation: true`: solo corren
  cuando se llaman por nombre (`/high-end-visual-design`, `/minimalist-ui`).
- `design-taste-frontend` trae scripts de Node 22+ que abren Chrome headless y buscan imágenes en
  colecciones abiertas. Escriben en `.taste-skill/` (ignorado en git) y en `~/.taste-skill/history.json`.
- Las carpetas `agents/openai.yaml` son configuración de Codex; Claude Code las ignora.
