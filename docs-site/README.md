# PayLink docs site

The source of https://websvcin.github.io/paylink/, built with [Docusaurus](https://docusaurus.io/) like the LoginLink and CrudApi docs. `.github/workflows/deploy-docs.yml` builds and publishes it to GitHub Pages whenever `docs-site/` changes on `main`.

```bash
cd docs-site
npm ci
npm start          # http://localhost:3000/paylink/
npm run build      # static site in build/; fails on broken links
```

- Pages are in `docs/`, the sidebar in `sidebars.js`, the home page in `src/pages/index.js`.
- `.md` files are plain Markdown; use `.mdx` only when a page needs React.
- **API reference** pages are generated from the OpenAPI documents in `static/openapi/`. After an API change, refresh them from a running PayLink and regenerate:

  ```bash
  for d in payments tenant platform; do curl -s https://your-paylink/docs/api/$d/openapi.json -o static/openapi/$d.json; done
  node scripts/generate-api-reference.mjs
  ```

- Screenshots are in `static/img/screenshots/`.
