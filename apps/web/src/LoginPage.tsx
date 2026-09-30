import { useState, type FormEvent } from "react";
import { useAuth } from "./AuthContext";
import "./AuthorizationPages.css";

export function LoginPage() {
  const { login, error, currentUser, loading } = useAuth();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setSubmitting(true); setMessage(null);
    try {
      await login(username, password);
      const requested = new URLSearchParams(window.location.search).get("returnTo");
      const target = requested?.startsWith("/") && !requested.startsWith("//") ? requested : "/";
      window.history.replaceState({}, "", target);
      window.dispatchEvent(new PopStateEvent("popstate"));
    } catch { setMessage("No se pudo iniciar sesión."); }
    finally { setSubmitting(false); setPassword(""); }
  };
  if (currentUser && !loading) return <main className="authorization-shell"><p role="status">Sesión iniciada como {currentUser.user.displayName}.</p><a href="/">Ir a HISPANIA</a></main>;
  return <main className="authorization-shell"><form className="auth-login" onSubmit={submit}>
    <span className="auth-eyebrow">HISPANIA</span><h1>Iniciar sesión</h1>
    <label>Usuario<input name="username" autoComplete="username" required value={username} onChange={event => setUsername(event.target.value)} /></label>
    <label>Contraseña<input name="password" type="password" autoComplete="current-password" required value={password} onChange={event => setPassword(event.target.value)} /></label>
    {(error || message) && <p role="alert">{error ?? message}</p>}
    <button type="submit" disabled={submitting}>{submitting ? "Entrando…" : "Entrar"}</button>
  </form></main>;
}
