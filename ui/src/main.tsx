import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

// Deliberately without <StrictMode>: in dev mode it would open and close every SSH/RDP session twice.
createRoot(document.getElementById('root')!).render(<App />);
