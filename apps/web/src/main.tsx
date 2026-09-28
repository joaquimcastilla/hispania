import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import "./App.css";
import "./Home.css";
import "./ModernTheme.css";

createRoot(document.getElementById("root")!).render(<StrictMode><App variant="modern" /></StrictMode>);
