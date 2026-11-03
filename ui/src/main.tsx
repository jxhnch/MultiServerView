import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

// Bewusst ohne <StrictMode>: Das würde im Dev-Modus jede SSH-/RDP-Session doppelt öffnen und wieder schliessen.
createRoot(document.getElementById('root')!).render(<App />);
