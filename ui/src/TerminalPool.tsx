import { useLayoutEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

/**
 * Terminals live outside the tabs that show them. Each one is rendered into its own element, and a <TerminalSlot> puts that
 * element where it belongs: into its tab, or into a pane of a split view. So moving a terminal between the two keeps the
 * same session and its scrollback (React would otherwise throw the terminal away and connect again).
 */
export function TerminalPool({ items, nodes }: { items: { id: string; content: ReactNode }[]; nodes: Map<string, HTMLDivElement> }) {
  const alive = new Set(items.map((i) => i.id));
  for (const id of [...nodes.keys()]) if (!alive.has(id)) nodes.delete(id);
  return <>{items.map((i) => createPortal(i.content, terminalNode(nodes, i.id), i.id))}</>;
}

export function terminalNode(nodes: Map<string, HTMLDivElement>, id: string): HTMLDivElement {
  let node = nodes.get(id);
  if (!node) {
    node = document.createElement('div');
    node.className = 'term-node';
    nodes.set(id, node);
  }
  return node;
}

export function TerminalSlot({ nodes, id }: { nodes: Map<string, HTMLDivElement>; id: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const node = terminalNode(nodes, id);
  // a layout effect: the element is on the page before the terminal inside it is set up or measured
  useLayoutEffect(() => {
    const slot = ref.current!;
    slot.appendChild(node);
    return () => {
      if (node.parentElement === slot) node.remove();
    };
  }, [node]);
  return <div className="term-slot" ref={ref} />;
}
