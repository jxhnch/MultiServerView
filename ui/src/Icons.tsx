import type { ReactNode } from 'react';
import type { Protocol } from './types';

type IconProps = { size?: number; className?: string };

function Svg({ size = 16, className, children }: IconProps & { children: ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden="true"
    >
      {children}
    </svg>
  );
}

export const IconTerminal = (p: IconProps) => (
  <Svg {...p}>
    <rect x="3" y="4" width="18" height="16" rx="3" />
    <path d="m7.5 9.5 3 2.5-3 2.5M13 15h4" />
  </Svg>
);
export const IconMonitor = (p: IconProps) => (
  <Svg {...p}>
    <rect x="3" y="4" width="18" height="12" rx="2.5" />
    <path d="M8 20h8M12 16v4" />
  </Svg>
);
export const IconScreen = (p: IconProps) => (
  <Svg {...p}>
    <rect x="3" y="4" width="18" height="13" rx="2.5" />
    <circle cx="12" cy="10.5" r="2.5" />
    <path d="M9 21h6" />
  </Svg>
);
export const IconKey = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="8" cy="15" r="4" />
    <path d="m10.8 12.2 8.2-8.2M16 7l3 3M14 9l2 2" />
  </Svg>
);
export const IconCloud = (p: IconProps) => (
  <Svg {...p}>
    <path d="M7 18a4.5 4.5 0 0 1-.6-8.96A6 6 0 0 1 18 9.5a4.25 4.25 0 0 1-.5 8.5H7Z" />
  </Svg>
);
export const IconServer = (p: IconProps) => (
  <Svg {...p}>
    <rect x="3" y="4" width="18" height="7" rx="2" />
    <rect x="3" y="13" width="18" height="7" rx="2" />
    <path d="M7 7.5h.01M7 16.5h.01" />
  </Svg>
);
export const IconLock = (p: IconProps) => (
  <Svg {...p}>
    <rect x="5" y="11" width="14" height="9" rx="2.5" />
    <path d="M8 11V8a4 4 0 0 1 8 0v3" />
  </Svg>
);
export const IconPlus = (p: IconProps) => (
  <Svg {...p}>
    <path d="M12 5v14M5 12h14" />
  </Svg>
);
export const IconSearch = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="11" cy="11" r="6.5" />
    <path d="m20 20-4-4" />
  </Svg>
);
export const IconStar = ({ filled, ...p }: IconProps & { filled?: boolean }) => (
  <Svg {...p}>
    <path
      d="m12 3.5 2.6 5.3 5.9.9-4.25 4.15 1 5.85L12 16.9l-5.25 2.8 1-5.85L3.5 9.7l5.9-.9L12 3.5Z"
      fill={filled ? 'currentColor' : 'none'}
    />
  </Svg>
);
export const IconEdit = (p: IconProps) => (
  <Svg {...p}>
    <path d="M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16v4Z" />
    <path d="m13.5 6.5 4 4" />
  </Svg>
);
export const IconClose = (p: IconProps) => (
  <Svg {...p}>
    <path d="m6 6 12 12M18 6 6 18" />
  </Svg>
);
export const IconRefresh = (p: IconProps) => (
  <Svg {...p}>
    <path d="M20 12a8 8 0 1 1-2.5-5.8M20 4v5h-5" />
  </Svg>
);
export const IconCopy = (p: IconProps) => (
  <Svg {...p}>
    <rect x="8" y="8" width="12" height="12" rx="2.5" />
    <path d="M16 8V6.5A2.5 2.5 0 0 0 13.5 4h-7A2.5 2.5 0 0 0 4 6.5v7A2.5 2.5 0 0 0 6.5 16H8" />
  </Svg>
);
export const IconTrash = (p: IconProps) => (
  <Svg {...p}>
    <path d="M4 7h16M9 7V4.5h6V7M6.5 7l1 13h9l1-13M10 11v6M14 11v6" />
  </Svg>
);
export const IconChevron = ({ open, ...p }: IconProps & { open?: boolean }) => (
  <Svg {...p}>
    <path d={open ? 'm6 9 6 6 6-6' : 'm9 6 6 6-6 6'} />
  </Svg>
);
export const IconUpload = (p: IconProps) => (
  <Svg {...p}>
    <path d="M12 16V4M7 9l5-5 5 5M4 17v1.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V17" />
  </Svg>
);
export const IconCheck = (p: IconProps) => (
  <Svg {...p}>
    <path d="m5 12.5 4.5 4.5L19 7.5" />
  </Svg>
);
export const IconEye = (p: IconProps) => (
  <Svg {...p}>
    <path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" />
    <circle cx="12" cy="12" r="2.8" />
  </Svg>
);
export const IconSun = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4" />
  </Svg>
);
export const IconMoon = (p: IconProps) => (
  <Svg {...p}>
    <path d="M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5Z" />
  </Svg>
);
export const IconFolder = (p: IconProps) => (
  <Svg {...p}>
    <path d="M3.5 7.5A2 2 0 0 1 5.5 5.5h4l2 2h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-9Z" />
  </Svg>
);
export const IconFile = (p: IconProps) => (
  <Svg {...p}>
    <path d="M14 3.5H7.5a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2V8l-4.5-4.5Z" />
    <path d="M14 3.5V8h4.5" />
  </Svg>
);
export const IconArrowUp = (p: IconProps) => (
  <Svg {...p}>
    <path d="M12 19V5M6 11l6-6 6 6" />
  </Svg>
);
export const IconArrowRight = (p: IconProps) => (
  <Svg {...p}>
    <path d="M5 12h14M13 6l6 6-6 6" />
  </Svg>
);
export const IconArrowLeft = (p: IconProps) => (
  <Svg {...p}>
    <path d="M19 12H5M11 6l-6 6 6 6" />
  </Svg>
);
export const IconDownload = (p: IconProps) => (
  <Svg {...p}>
    <path d="M12 4v12M7 11l5 5 5-5M4 19h16" />
  </Svg>
);
export const IconCode = (p: IconProps) => (
  <Svg {...p}>
    <path d="m8 7-5 5 5 5M16 7l5 5-5 5M14 5l-4 14" />
  </Svg>
);
export const IconForward = (p: IconProps) => (
  <Svg {...p}>
    <path d="M4 12h12M12 6l6 6-6 6M20 5v14" />
  </Svg>
);
export const IconShield = (p: IconProps) => (
  <Svg {...p}>
    <path d="M12 3.5 5 6v5.5c0 4.2 2.8 7.4 7 9 4.2-1.6 7-4.8 7-9V6l-7-2.5Z" />
    <path d="m9 12 2.2 2.2L15.5 10" />
  </Svg>
);
export const IconClock = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 7.5V12l3 2" />
  </Svg>
);
export const IconPlay = (p: IconProps) => (
  <Svg {...p}>
    <path d="M8 5.5v13l10.5-6.5L8 5.5Z" />
  </Svg>
);
export const IconStop = (p: IconProps) => (
  <Svg {...p}>
    <rect x="6.5" y="6.5" width="11" height="11" rx="2" />
  </Svg>
);
export const IconText = (p: IconProps) => (
  <Svg {...p}>
    <path d="M4 7V5.5h16V7M12 5.5v13M9 18.5h6" />
  </Svg>
);
export const IconUser = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="12" cy="8.5" r="3.5" />
    <path d="M5 20c.8-3.6 3.6-5.5 7-5.5s6.2 1.9 7 5.5" />
  </Svg>
);
export const IconCommand = (p: IconProps) => (
  <Svg {...p}>
    <path d="M9 6.5a2.5 2.5 0 1 0-2.5 2.5H9m0-2.5V9m0-2.5h6M9 9v6m0-6h6m0 0V6.5A2.5 2.5 0 1 1 17.5 9H15m0 0v6m0 0h2.5a2.5 2.5 0 1 1-2.5 2.5V15m0 0H9m0 0v2.5A2.5 2.5 0 1 1 6.5 15H9" />
  </Svg>
);
export const IconDuplicate = (p: IconProps) => (
  <Svg {...p}>
    <rect x="4" y="8" width="12" height="12" rx="2.5" />
    <path d="M9 4h9.5A1.5 1.5 0 0 1 20 5.5V15" />
  </Svg>
);

/** Passendes Icon zum Protokoll. */
export function ProtocolIcon({ protocol, size = 16 }: { protocol: Protocol; size?: number }) {
  if (protocol === 'rdp') return <IconMonitor size={size} />;
  if (protocol === 'vnc') return <IconScreen size={size} />;
  return <IconTerminal size={size} />;
}
