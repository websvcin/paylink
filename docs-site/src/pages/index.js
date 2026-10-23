import React from 'react';
import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import HomepageFeatures from '@site/src/components/HomepageFeatures';
import styles from './index.module.css';

function HomepageHero() {
  return (
    <header className={styles.hero}>
      <h1 className={styles.heroTitle}>One integration. Every payment method becomes a setting.</h1>
      <p className={styles.heroSubtitle}>
        PayLink is a self-hosted, multi-tenant payments platform. Start today with free PayLink UPI, add Razorpay,
        PhonePe, PayU, CCAvenue, Stripe or PayPal when you're ready, and run it all from one console and one API.
      </p>
      <div className={styles.heroActions}>
        <Link className="button button--lg" style={{ background: 'var(--pl-accent)', color: '#fff', border: 'none' }} to="/getting-started/quickstart">
          Get started →
        </Link>
        <Link className="button button--lg button--outline" to="/api-reference/overview">
          API Reference
        </Link>
      </div>
      <p className={styles.heroSubtitle} style={{ marginTop: '1rem', fontSize: '0.95rem' }}>
        New here? <Link to="/getting-started/who-paylink-is-for">See who PayLink is for →</Link>
      </p>
    </header>
  );
}

export default function Home() {
  const { siteConfig } = useDocusaurusContext();
  return (
    <Layout
      title={siteConfig.title}
      description="Guides and API references for PayLink: take payments with UPI and every major Indian and international gateway through one integration, on your own servers.">
      <HomepageHero />
      <main>
        <HomepageFeatures />
      </main>
    </Layout>
  );
}
