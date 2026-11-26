/**
 * Dragging with the pointer instead of the browser's own drag and drop: the dragged element is lifted and follows the pointer,
 * and the list can rearrange itself under it (animated by useFlip) to show where it will land.
 */
import { useLayoutEffect, useRef, type PointerEvent as ReactPointerEvent, type RefObject } from 'react';

const calm = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const GLIDE = 'cubic-bezier(0.2, 0.8, 0.2, 1)';

/**
 * Lets every element with a data-flip attribute glide from its old place to its new one after a render. The container must be
 * the offset parent (position: relative), so scrolling does not count as movement.
 */
export function useFlip(container: RefObject<HTMLElement | null>) {
  const last = useRef({ width: 0, places: new Map<string, { x: number; y: number }>() });
  useLayoutEffect(() => {
    const root = container.current;
    if (!root || !root.offsetWidth) return; // hidden: keep what was seen last
    const places = new Map<string, { x: number; y: number }>();
    // after a resize everything moves, which should not be animated
    const still = calm() || last.current.width !== root.offsetWidth;
    for (const el of root.querySelectorAll<HTMLElement>('[data-flip]')) {
      const key = el.dataset.flip!;
      const now = { x: el.offsetLeft, y: el.offsetTop };
      places.set(key, now);
      const before = last.current.places.get(key);
      if (still || !before || (Math.abs(before.x - now.x) < 2 && Math.abs(before.y - now.y) < 2)) continue;
      el.animate([{ transform: `translate(${before.x - now.x}px, ${before.y - now.y}px)` }, { transform: 'translate(0, 0)' }], { duration: 240, easing: GLIDE });
    }
    last.current = { width: root.offsetWidth, places };
  });
}

export interface DragHandlers {
  /** More than 1: a badge shows how many are dragged. */
  count?: number;
  onStart: () => void;
  /**
   * The pointer moved (or the list scrolled); "hit" is what lies under it. Return false when this position could not be looked
   * at yet (an animation is running): it is then asked about again shortly, and at the latest when the button is released
   * ("now" = true, answer whatever is going on), so a fast movement is not lost.
   */
  onOver: (hit: Element | null, x: number, y: number, now: boolean) => boolean | void;
  onDrop: (hit: Element | null) => void;
  onCancel: () => void;
  /** Where the lifted element flies to after the drop (or nowhere: it fades out). */
  landing: () => Element | null;
}

function scrollParent(el: Element): Element {
  for (let p = el.parentElement; p; p = p.parentElement) {
    const o = getComputedStyle(p).overflowY;
    if ((o === 'auto' || o === 'scroll') && p.scrollHeight > p.clientHeight) return p;
  }
  return document.scrollingElement ?? document.documentElement;
}

/** Call on pointerdown. A drag only starts after a few pixels of movement, so clicks keep working. */
export function pointerDrag(down: ReactPointerEvent, source: HTMLElement, handlers: DragHandlers) {
  if (down.button !== 0 || down.pointerType === 'touch' || (down.target as Element).closest('button, input, select, textarea')) return;
  const startX = down.clientX;
  const startY = down.clientY;
  const scroller = scrollParent(source);
  let ghost: HTMLElement | null = null;
  let grab = { x: 0, y: 0 };
  let origin = { x: 0, y: 0 };
  let at = { x: startX, y: startY };
  let frame = 0;

  let retry = 0;

  const under = () => document.elementFromPoint(at.x, at.y);

  const look = (now = false) => {
    window.clearTimeout(retry);
    retry = 0;
    if (handlers.onOver(under(), at.x, at.y, now) === false) retry = window.setTimeout(look, 60);
  };

  const lift = () => {
    const r = source.getBoundingClientRect();
    grab = { x: startX - r.left, y: startY - r.top };
    origin = { x: r.left, y: r.top };
    ghost = source.cloneNode(true) as HTMLElement;
    ghost.removeAttribute('data-flip');
    ghost.removeAttribute('data-host');
    ghost.classList.add('drag-ghost');
    Object.assign(ghost.style, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` });
    if ((handlers.count ?? 1) > 1) {
      const badge = document.createElement('span');
      badge.className = 'drag-count';
      badge.textContent = String(handlers.count);
      ghost.append(badge);
    }
    // inside the app element, so the theme's colours apply
    (source.closest('.app') ?? document.body).append(ghost);
    document.body.classList.add('is-dragging');
    window.getSelection()?.removeAllRanges();
    handlers.onStart();
    frame = requestAnimationFrame(autoScroll);
  };

  const follow = () => {
    if (ghost) ghost.style.translate = `${at.x - grab.x - origin.x}px ${at.y - grab.y - origin.y}px`;
  };

  // near the top or bottom edge the list scrolls by itself
  const autoScroll = () => {
    const box = scroller === document.scrollingElement ? { top: 0, bottom: window.innerHeight } : scroller.getBoundingClientRect();
    const edge = 70;
    const speed = at.y < box.top + edge ? -Math.ceil((box.top + edge - at.y) / 6) : at.y > box.bottom - edge ? Math.ceil((at.y - (box.bottom - edge)) / 6) : 0;
    if (speed && !under()?.closest('[data-zone]')) {
      const before = scroller.scrollTop;
      scroller.scrollTop += speed;
      if (scroller.scrollTop !== before) look();
    }
    frame = requestAnimationFrame(autoScroll);
  };

  const move = (e: PointerEvent) => {
    at = { x: e.clientX, y: e.clientY };
    if (!ghost) {
      if (Math.hypot(at.x - startX, at.y - startY) < 6) return;
      lift();
    }
    e.preventDefault();
    follow();
    look();
  };

  const finish = (dropped: boolean) => {
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', up);
    window.removeEventListener('pointercancel', cancel);
    window.removeEventListener('blur', cancel);
    window.removeEventListener('keydown', key, true);
    cancelAnimationFrame(frame);
    if (!ghost) return;
    if (dropped && retry) look(true);
    window.clearTimeout(retry);
    document.body.classList.remove('is-dragging');
    // the click that follows the release of the button must not select or open anything
    const swallow = (e: Event) => {
      e.stopPropagation();
      e.preventDefault();
    };
    window.addEventListener('click', swallow, true);
    window.setTimeout(() => window.removeEventListener('click', swallow, true), 0);
    if (dropped) handlers.onDrop(under());
    else handlers.onCancel();
    land(ghost);
    ghost = null;
  };

  const land = (flying: HTMLElement) => {
    // one frame later, when the list shows its final order
    requestAnimationFrame(() => {
      const target = handlers.landing();
      if (calm()) return flying.remove();
      flying.classList.add('landing');
      if (target) {
        const r = target.getBoundingClientRect();
        flying.style.translate = `${r.left - origin.x}px ${r.top - origin.y}px`;
      } else flying.style.opacity = '0';
      window.setTimeout(() => flying.remove(), 230);
    });
  };

  const up = () => finish(true);
  const cancel = () => finish(false);
  const key = (e: KeyboardEvent) => {
    if (e.key !== 'Escape') return;
    e.stopPropagation();
    finish(false);
  };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', up);
  window.addEventListener('pointercancel', cancel);
  window.addEventListener('blur', cancel);
  window.addEventListener('keydown', key, true);
}
