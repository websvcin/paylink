// @ts-check
// PayLink's public documentation, deployed to GitHub Pages (websvcin.github.io/paylink/) by
// .github/workflows/deploy-docs.yml whenever docs-site/ changes on main. Same layout as the
// LoginLink and CrudApi docs. Run locally with `npm ci && npm start` in this folder.

const lightCodeTheme = require('prism-react-renderer').themes.github;
const darkCodeTheme = require('prism-react-renderer').themes.dracula;

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'PayLink Docs',
  tagline: 'One integration for your app. Every payment method becomes a setting.',
  favicon: 'img/favicon.svg',

  url: 'https://websvcin.github.io',
  baseUrl: '/paylink/',

  organizationName: 'websvcin',
  projectName: 'paylink',

  onBrokenLinks: 'throw',

  markdown: {
    // .md files are plain CommonMark (paths like /{slug}/console need no escaping); .mdx files are MDX.
    format: 'detect',
    hooks: { onBrokenMarkdownLinks: 'throw' },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          sidebarPath: require.resolve('./sidebars.js'),
          editUrl: 'https://github.com/websvcin/paylink/edit/main/docs-site/',
          routeBasePath: '/',
        },
        blog: false,
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: 'img/social-card.png',
      colorMode: {
        defaultMode: 'light',
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'PayLink Docs',
        logo: {
          alt: 'PayLink',
          src: 'img/logo.svg',
        },
        items: [
          { type: 'docSidebar', sidebarId: 'docsSidebar', position: 'left', label: 'Docs' },
          { to: '/integrate/payments-api', position: 'left', label: 'Integrate' },
          { to: '/api-reference/overview', position: 'left', label: 'API Reference' },
          { to: '/changelog', position: 'left', label: 'Changelog' },
          { href: 'https://github.com/websvcin/paylink', label: 'GitHub', position: 'right' },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              { label: 'Quickstart', to: '/getting-started/quickstart' },
              { label: 'Payments API', to: '/integrate/payments-api' },
              { label: 'Automation API', to: '/automation/tenant-api' },
              { label: 'API Reference', to: '/api-reference/overview' },
            ],
          },
          {
            title: 'Run PayLink',
            items: [
              { label: 'Docker', to: '/self-hosting/docker' },
              { label: 'First-run setup', to: '/self-hosting/first-run-setup' },
              { label: 'Platform admin guide', to: '/self-hosting/platform-admin-guide' },
            ],
          },
          {
            title: 'Family',
            items: [
              { label: 'LoginLink docs', href: 'https://websvcin.github.io/loginlink/' },
              { label: 'CrudApi docs', href: 'https://websvcin.github.io/crudapi/' },
              { label: 'GitHub', href: 'https://github.com/websvcin/paylink' },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} PayLink.`,
      },
      prism: {
        theme: lightCodeTheme,
        darkTheme: darkCodeTheme,
        additionalLanguages: ['csharp', 'bash', 'json', 'yaml', 'php', 'python', 'java'],
      },
    }),
};

module.exports = config;
