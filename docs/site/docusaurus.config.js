const config = {
  title: 'Agentstration',
  tagline: 'Govern, execute, and track agent work — locally first.',
  url: 'https://docs.agentstration.io',
  baseUrl: '/',
  trailingSlash: false,
  organizationName: 'gbaudrit',
  projectName: 'microsoft-agent-framework',
  onBrokenLinks: 'throw',
  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },
  themes: ['@docusaurus/theme-mermaid'],
  presets: [
    [
      'classic',
      {
        docs: {
          path: '..',
          routeBasePath: '/',
          sidebarPath: './sidebars.js',
          exclude: ['site/**'],
          showLastUpdateTime: true,
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      },
    ],
  ],
  themeConfig: {
    colorMode: {
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'Agentstration',
      items: [
        {to: '/getting-started/overview', label: 'Get started', position: 'left'},
        {to: '/concepts/overview', label: 'Concepts', position: 'left'},
        {to: '/architecture/overview', label: 'Architecture', position: 'left'},
        {to: '/reference/overview', label: 'Reference', position: 'left'},
        {to: '/decisions', label: 'ADRs', position: 'left'},
        {
          href: 'https://github.com/gbaudrit/microsoft-agent-framework',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Documentation',
          items: [
            {label: 'Get started', to: '/getting-started/overview'},
            {label: 'Architecture', to: '/architecture/overview'},
            {label: 'Versioning', to: '/reference/versioning'},
          ],
        },
        {
          title: 'Project',
          items: [
            {label: 'Contributing', to: '/contributing/overview'},
            {label: 'GitHub', href: 'https://github.com/gbaudrit/microsoft-agent-framework'},
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Agentstration contributors.`,
    },
    mermaid: {
      theme: {light: 'neutral', dark: 'dark'},
    },
  },
};

export default config;
