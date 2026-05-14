/* global React */
const { useEffect, useRef } = React;

/**
 * Abstract wearable thermal device.
 * - Stylized pebble / collar shape (NOT Sony's actual Reon Pocket design).
 * - The thermal plate in the center glows blue when cooling, orange when heating,
 *   intensity scales with level.
 * - Tiny LED row indicates level.
 */
function ReonDevice({ mode = "Stop", level = 0, plate = 31.37, ambient = 30.69 }) {
  const isCool = mode === "Cool";
  const isHeat = mode === "Heat";
  const isOff = !isCool && !isHeat;
  const intensity = Math.min(1, level / 4);

  const cool = "#4ab8ff";
  const heat = "#ff7a3d";
  const accent = isCool ? cool : isHeat ? heat : "#3a4250";
  const accent2 = isCool ? "#6bd6ff" : isHeat ? "#ffb061" : "#525a68";

  // Pulse breath for active modes
  const breathRef = useRef(null);
  useEffect(() => {
    if (!breathRef.current) return;
    if (isOff) {
      breathRef.current.style.animation = "none";
    } else {
      breathRef.current.style.animation = `devicebreath ${isHeat ? 2.8 : 3.6}s ease-in-out infinite`;
    }
  }, [isOff, isHeat, isCool]);

  const glowOpacity = isOff ? 0.10 : 0.35 + intensity * 0.45;
  const plateOpacity = isOff ? 0.55 : 0.65 + intensity * 0.35;

  return (
    <svg viewBox="0 0 400 320" className="device-svg" xmlns="http://www.w3.org/2000/svg">
      <defs>
        <radialGradient id="ambient-glow" cx="50%" cy="48%" r="50%">
          <stop offset="0%" stopColor={accent} stopOpacity={glowOpacity} />
          <stop offset="55%" stopColor={accent} stopOpacity={glowOpacity * 0.18} />
          <stop offset="100%" stopColor={accent} stopOpacity={0} />
        </radialGradient>

        <linearGradient id="body-grad" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#2a313d" />
          <stop offset="100%" stopColor="#161a22" />
        </linearGradient>

        <linearGradient id="body-edge" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#3a4150" stopOpacity="0.9" />
          <stop offset="100%" stopColor="#1a1e26" stopOpacity="0.4" />
        </linearGradient>

        <radialGradient id="plate-grad" cx="50%" cy="50%" r="55%">
          <stop offset="0%" stopColor={accent2} stopOpacity={plateOpacity} />
          <stop offset="50%" stopColor={accent} stopOpacity={plateOpacity * 0.85} />
          <stop offset="100%" stopColor={accent} stopOpacity={plateOpacity * 0.2} />
        </radialGradient>

        <linearGradient id="plate-rim" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#454c5b" />
          <stop offset="100%" stopColor="#2a2f3a" />
        </linearGradient>

        <linearGradient id="hi-light" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="white" stopOpacity="0.10" />
          <stop offset="100%" stopColor="white" stopOpacity="0" />
        </linearGradient>

        <filter id="soft-blur" x="-50%" y="-50%" width="200%" height="200%">
          <feGaussianBlur stdDeviation="14" />
        </filter>

        <filter id="plate-blur" x="-50%" y="-50%" width="200%" height="200%">
          <feGaussianBlur stdDeviation="3" />
        </filter>
      </defs>

      {/* Ambient halo */}
      <ellipse cx="200" cy="155" rx="170" ry="115" fill="url(#ambient-glow)" />

      {/* Soft blurred halo for active state */}
      {!isOff && (
        <ellipse
          cx="200"
          cy="160"
          rx="95"
          ry="65"
          fill={accent}
          opacity={0.25 + intensity * 0.3}
          filter="url(#soft-blur)"
          ref={breathRef}
        />
      )}

      {/* Device body — pebble / wearable pod */}
      <g>
        {/* main body */}
        <path
          d="M 95 145
             Q 95 95, 145 88
             L 255 88
             Q 305 95, 305 145
             L 305 175
             Q 305 225, 255 232
             L 145 232
             Q 95 225, 95 175
             Z"
          fill="url(#body-grad)"
          stroke="url(#body-edge)"
          strokeWidth="1.2"
        />

        {/* top highlight */}
        <path
          d="M 110 110
             Q 130 100, 200 98
             Q 270 100, 290 110
             Q 285 116, 200 116
             Q 115 116, 110 110 Z"
          fill="url(#hi-light)"
        />

        {/* Side vents (left) */}
        <g opacity="0.6">
          <rect x="108" y="135" width="2" height="40" rx="1" fill="#0a0d12" />
          <rect x="113" y="140" width="2" height="30" rx="1" fill="#0a0d12" />
          <rect x="118" y="145" width="2" height="20" rx="1" fill="#0a0d12" />
        </g>
        {/* Side vents (right) */}
        <g opacity="0.6">
          <rect x="290" y="135" width="2" height="40" rx="1" fill="#0a0d12" />
          <rect x="285" y="140" width="2" height="30" rx="1" fill="#0a0d12" />
          <rect x="280" y="145" width="2" height="20" rx="1" fill="#0a0d12" />
        </g>

        {/* Thermal plate frame */}
        <rect
          x="148" y="128" width="104" height="64" rx="14"
          fill="url(#plate-rim)"
          stroke="#0a0d12" strokeWidth="0.8"
        />
        {/* Inner glow plate */}
        <rect
          x="153" y="133" width="94" height="54" rx="11"
          fill="url(#plate-grad)"
          filter={isOff ? undefined : "url(#plate-blur)"}
        />
        {/* Plate surface (slight gloss) */}
        <rect
          x="153" y="133" width="94" height="54" rx="11"
          fill="none"
          stroke={accent}
          strokeWidth={isOff ? 0.5 : 1.2}
          opacity={isOff ? 0.3 : 0.85}
        />
        {/* Plate highlight */}
        <rect
          x="156" y="136" width="88" height="14" rx="7"
          fill="white" opacity="0.06"
        />

        {/* Level LEDs */}
        <g transform="translate(170, 210)">
          {[1, 2, 3, 4].map((i) => {
            const on = !isOff && i <= level;
            return (
              <circle
                key={i}
                cx={(i - 1) * 20}
                cy={0}
                r={3}
                fill={on ? accent2 : "#1f242d"}
                stroke={on ? accent : "#2a2f39"}
                strokeWidth="0.6"
              >
                {on && (
                  <animate
                    attributeName="opacity"
                    values="0.7;1;0.7"
                    dur={`${2 + i * 0.2}s`}
                    repeatCount="indefinite"
                  />
                )}
              </circle>
            );
          })}
        </g>

        {/* Brand tick */}
        <circle cx="200" cy="100" r="1.6" fill="#5a6271" />

        {/* Sensor dot bottom-left */}
        <circle cx="120" cy="218" r="1.6" fill={isOff ? "#404652" : accent2} opacity="0.7" />
      </g>

      {/* Floating frost particles when cooling */}
      {isCool && intensity > 0.25 && (
        <g opacity={intensity * 0.7}>
          {Array.from({ length: 6 }).map((_, i) => (
            <circle
              key={i}
              cx={130 + i * 25}
              cy={70 + (i % 2) * 14}
              r={1.5}
              fill={cool}
            >
              <animate
                attributeName="cy"
                values={`${70 + (i % 2) * 14}; ${40 + (i % 2) * 14}; ${70 + (i % 2) * 14}`}
                dur={`${4 + i * 0.4}s`}
                repeatCount="indefinite"
              />
              <animate
                attributeName="opacity"
                values="0; 0.9; 0"
                dur={`${4 + i * 0.4}s`}
                repeatCount="indefinite"
              />
            </circle>
          ))}
        </g>
      )}

      {/* Rising heat shimmer when heating */}
      {isHeat && intensity > 0.25 && (
        <g opacity={intensity * 0.6}>
          {Array.from({ length: 5 }).map((_, i) => (
            <path
              key={i}
              d={`M ${150 + i * 25} 130 Q ${152 + i * 25} 110, ${150 + i * 25} 90`}
              stroke={heat}
              strokeWidth="1.2"
              fill="none"
              strokeLinecap="round"
            >
              <animate
                attributeName="opacity"
                values="0; 0.8; 0"
                dur={`${2.4 + i * 0.3}s`}
                repeatCount="indefinite"
                begin={`${i * 0.4}s`}
              />
            </path>
          ))}
        </g>
      )}

      {/* Temperature label tag */}
      <g transform="translate(200, 280)">
        <text
          textAnchor="middle"
          fontFamily="Geist Mono, ui-monospace, monospace"
          fontSize="11"
          fill="#6b7385"
          letterSpacing="0.5"
        >
          PLATE {plate.toFixed(2)}°C · AMBIENT {ambient.toFixed(2)}°C
        </text>
      </g>

      <style>{`
        @keyframes devicebreath {
          0%, 100% { opacity: ${0.22 + intensity * 0.25}; transform: scale(1); transform-origin: 200px 160px; }
          50% { opacity: ${0.42 + intensity * 0.35}; transform: scale(1.06); transform-origin: 200px 160px; }
        }
      `}</style>
    </svg>
  );
}

window.ReonDevice = ReonDevice;
