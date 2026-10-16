import { createRoot } from 'react-dom/client';
import '@canvas/theme.generated.css';
import './court.css';
import { App } from './App';

createRoot(document.getElementById('root')!).render(<App />);
