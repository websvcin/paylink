import React from 'react';
import Link from '@docusaurus/Link';
import styles from './styles.module.css';

const Icon = ({ path, color }) => (
  <div className={styles.icon} style={{ background: color.soft, color: color.ink }}>
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
      <path strokeLinecap="round" strokeLinejoin="round" d={path} />
    </svg>
  </div>
);

const blue = { soft: 'var(--pl-accent-soft)', ink: 'var(--pl-accent-ink)' };
const mint = { soft: 'var(--pl-mint-soft)', ink: 'var(--pl-mint)' };
const amber = { soft: 'rgba(180, 83, 9, 0.12)', ink: '#b45309' };

const FeatureList = [
  {
    title: 'Getting Started',
    color: blue,
    path: 'M13 10V3L4 14h7v7l9-11h-7z',
    description: 'What PayLink is, who it is for, how a payment flows, and a ten-minute quickstart from docker run to a paid test payment.',
    links: [
      { label: 'Who PayLink is for', to: '/getting-started/who-paylink-is-for' },
      { label: 'How it works', to: '/getting-started/how-it-works' },
      { label: 'Quickstart', to: '/getting-started/quickstart' },
    ],
  },
  {
    title: 'Payment Methods',
    color: mint,
    path: 'M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z',
    description: 'Free PayLink UPI into your own UPI ID, plus Razorpay, PhonePe, PayU, CCAvenue, Stripe and PayPal behind one API.',
    links: [
      { label: 'PayLink UPI', to: '/payment-methods/paylink-upi' },
      { label: 'All methods', to: '/payment-methods/overview' },
    ],
  },
  {
    title: 'Integrate',
    color: blue,
    path: 'M10 20l4-16m4 4l4 4-4 4M6 8l-4 4 4 4',
    description: 'Create a payment, send the payer to checkout, trust the signed webhook. Payment links, a Pay button, refunds and reporting.',
    links: [
      { label: 'Payments API', to: '/integrate/payments-api' },
      { label: 'Webhooks', to: '/integrate/webhooks' },
      { label: 'Pay button', to: '/integrate/pay-button' },
    ],
  },
  {
    title: 'Automation',
    color: amber,
    path: 'M5 12h14M5 12a2 2 0 01-2-2V8a2 2 0 012-2h14a2 2 0 012 2v2a2 2 0 01-2 2M5 12a2 2 0 00-2 2v2a2 2 0 002 2h14a2 2 0 002-2v-2a2 2 0 00-2-2',
    description: 'Everything the console does, from scripts: UPI approvals and bank statements, links, methods, team. Build a product on PayLink with one platform key.',
    links: [
      { label: 'Tenant API', to: '/automation/tenant-api' },
      { label: 'Building on PayLink', to: '/automation/building-on-paylink' },
    ],
  },
  {
    title: 'Tenant Console',
    color: mint,
    path: 'M17 20h5v-2a4 4 0 00-3-3.87M9 20H4v-2a4 4 0 013-3.87m6-1.13a4 4 0 10-4-4 4 4 0 004 4zm6 0a4 4 0 10-4-4',
    description: 'Every page a merchant uses: payments and journeys, UPI verification, links, risk rules, reports, reconciliation, team and keys.',
    links: [
      { label: 'Tenant console guide', to: '/console/tenant-admin-guide' },
      { label: 'UPI verification', to: '/console/upi-verification' },
    ],
  },
  {
    title: 'Self-Hosting & Platform Admin',
    color: amber,
    path: 'M21 16V8a2 2 0 00-1-1.73l-7-4a2 2 0 00-2 0l-7 4A2 2 0 003 8v8a2 2 0 001 1.73l7 4a2 2 0 002 0l7-4A2 2 0 0021 16z',
    description: 'Run PayLink with Docker, set it up, and run the platform: tenants, connector catalog, partner onboarding, databases, backups.',
    links: [
      { label: 'Docker', to: '/self-hosting/docker' },
      { label: 'Platform admin guide', to: '/self-hosting/platform-admin-guide' },
    ],
  },
];

function Feature({ title, color, path, description, links }) {
  return (
    <div className={styles.card}>
      <Icon path={path} color={color} />
      <h3>{title}</h3>
      <p>{description}</p>
      <div className={styles.links}>
        {links.map((l) => (
          <Link key={l.to} to={l.to}>
            {l.label} →
          </Link>
        ))}
      </div>
    </div>
  );
}

export default function HomepageFeatures() {
  return (
    <section className={styles.grid}>
      {FeatureList.map((props) => (
        <Feature key={props.title} {...props} />
      ))}
    </section>
  );
}
