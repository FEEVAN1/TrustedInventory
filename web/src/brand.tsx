// Brand mark and small line icons, drawn inline: the page policy allows no image sources but the app itself (DEC-104).
import type { ReactNode } from "react";

/** The logo: a stacked box with a check, in the app's blues. Decorative; the name next to it is the accessible text. */
export function Logo({ size = 32 }: { size?: number }) {
  return (
    <svg className="logo" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true" focusable="false">
      <rect x="1" y="1" width="30" height="30" rx="8" fill="#0a5fa8" />
      <path d="M16 6.5 25 11v10l-9 4.5L7 21V11z" fill="#e3f1fd" />
      <path d="M7 11l9 4.5 9-4.5M16 15.5V25.5" fill="none" stroke="#7cb8ea" strokeWidth="1.4" strokeLinejoin="round" />
      <path d="m11.8 18.2 2.6 2.6 5-5.2" fill="none" stroke="#0a5fa8" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** Logo and product name for the top of every surface; <c>suffix</c> names a separate surface such as the platform console. */
export function Brand({ suffix, href }: { suffix?: string; href?: string }) {
  const content = <><Logo /><strong>TrustedInventory{suffix ? ` ${suffix}` : ""}</strong></>;
  return href ? <a href={href} className="brand">{content}</a> : <span className="brand">{content}</span>;
}

const PATHS: Record<string, ReactNode> = {
  scan: <><path d="M4 7V5a1 1 0 0 1 1-1h2M17 4h2a1 1 0 0 1 1 1v2M20 17v2a1 1 0 0 1-1 1h-2M7 20H5a1 1 0 0 1-1-1v-2" /><path d="M8 8v8M11 8v8M14 8v8M17 8v8" /></>,
  truck: <><path d="M3 6h11v10H3zM14 10h4l3 3v3h-7" /><circle cx="7" cy="17.5" r="1.8" /><circle cx="17" cy="17.5" r="1.8" /></>,
  approve: <><circle cx="9" cy="8" r="3" /><path d="M3 19c0-3 2.7-5 6-5s6 2 6 5" /><path d="m15 11 2 2 4-4" /></>,
  shield: <><path d="M12 3 5 6v5c0 4.4 3 8.3 7 10 4-1.7 7-5.6 7-10V6z" /><path d="m9 12 2 2 4-4" /></>,
  recall: <><path d="M12 4 2.8 19.5h18.4z" /><path d="M12 10v4.5M12 17.2v.1" /></>,
  thermo: <><path d="M10 14.5V5a2 2 0 1 1 4 0v9.5a4 4 0 1 1-4 0z" /><path d="M12 9v7" /></>,
  ledger: <><path d="M6 3h10l3 3v15H6z" /><path d="M9 9h7M9 13h7M9 17h4" /></>,
  phone: <><rect x="7" y="2.5" width="10" height="19" rx="2" /><path d="M11 18.5h2" /></>,
};

/** A decorative line icon in a tinted circle. */
export function Icon({ name }: { name: keyof typeof PATHS }) {
  return (
    <span className="icon" aria-hidden="true">
      <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" focusable="false">
        {PATHS[name]}
      </svg>
    </span>
  );
}

/** Hero illustration: a shelf of boxes with a scanner beam and a ledger card. Decorative only. */
export function HeroArt() {
  return (
    <svg className="hero-art" viewBox="0 0 420 320" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id="sky" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#d4eafc" />
          <stop offset="1" stopColor="#a9d3f5" />
        </linearGradient>
      </defs>
      <rect x="10" y="10" width="400" height="300" rx="28" fill="url(#sky)" />
      <rect x="45" y="70" width="230" height="10" rx="4" fill="#5d9fd6" />
      <rect x="45" y="170" width="230" height="10" rx="4" fill="#5d9fd6" />
      <rect x="45" y="270" width="230" height="10" rx="4" fill="#5d9fd6" />
      <rect x="55" y="25" width="60" height="45" rx="6" fill="#ffffff" />
      <rect x="125" y="35" width="50" height="35" rx="6" fill="#e3f1fd" />
      <rect x="185" y="20" width="70" height="50" rx="6" fill="#ffffff" />
      <rect x="55" y="120" width="70" height="50" rx="6" fill="#e3f1fd" />
      <rect x="135" y="115" width="55" height="55" rx="6" fill="#ffffff" />
      <rect x="200" y="130" width="60" height="40" rx="6" fill="#e3f1fd" />
      <rect x="60" y="225" width="55" height="45" rx="6" fill="#ffffff" />
      <rect x="125" y="215" width="75" height="55" rx="6" fill="#e3f1fd" />
      <rect x="210" y="230" width="50" height="40" rx="6" fill="#ffffff" />
      <path d="M150 135h25M150 143h25M150 151h15" stroke="#0a5fa8" strokeWidth="3" strokeLinecap="round" />
      <path d="M200 52 L330 120" stroke="#f2a900" strokeWidth="3" strokeDasharray="6 6" />
      <rect x="300" y="95" width="80" height="150" rx="16" fill="#0a5fa8" />
      <rect x="310" y="110" width="60" height="85" rx="8" fill="#e3f1fd" />
      <path d="M320 125h40M320 137h28M320 149h36M320 161h22" stroke="#5d9fd6" strokeWidth="4" strokeLinecap="round" />
      <path d="m323 178 7 7 13-13" fill="none" stroke="#1d7a3a" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="340" cy="220" r="10" fill="#7cb8ea" />
    </svg>
  );
}
