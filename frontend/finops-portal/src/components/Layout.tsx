import { NavLink, Outlet } from 'react-router-dom';
import { LayoutDashboard, Lightbulb, AlertTriangle, Database, Boxes, Users, PieChart, Activity, ArrowRightLeft } from 'lucide-react';
import { useAccess } from '../auth/AccessContext';
import Brand from './Brand';
import ErrorBoundary from './ErrorBoundary';
import UserProfile from './UserProfile';

const navItems = [
  { to: '/', icon: LayoutDashboard, label: 'Dashboard' },
  { to: '/recommendations', icon: Lightbulb, label: 'Recomendações' },
  { to: '/resources', icon: Boxes, label: 'Recursos e custos' },
  { to: '/allocation', icon: PieChart, label: 'Rateio' },
  { to: '/anomalies', icon: AlertTriangle, label: 'Anomalias' },
  { to: '/databases', icon: Database, label: 'Bancos de Dados' },
  { to: '/ownership', icon: Users, label: 'Ownership' },
];

export default function Layout() {
  const { isAdmin } = useAccess();
  return (
    <div className="layout layout--sidebar-hover">
      <aside className="sidebar">
        <div className="sidebar-header">
          <Brand />
        </div>
        <nav className="sidebar-nav">
          {navItems.map(({ to, icon: Icon, label }) => (
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`}
              end={to === '/'}
              title={label}
            >
              <Icon size={18} />
              <span className="nav-label">{label}</span>
            </NavLink>
          ))}
          {isAdmin && <NavLink to="/data-health" className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`} title="Saúde dos dados · Admin">
            <Activity size={18} /><span className="nav-label">Saúde dos dados · Admin</span>
          </NavLink>}
          {isAdmin && <NavLink to="/migrations" className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`} title="Migrações · Admin">
            <ArrowRightLeft size={18} /><span className="nav-label">Migrações · Admin</span>
          </NavLink>}
        </nav>
        <div className="sidebar-footer">
          <UserProfile />
        </div>
      </aside>
      <main className="main-content">
        {/* Keeps the sidebar usable when a single page fails, instead of blanking the app. */}
        <ErrorBoundary>
          <Outlet />
        </ErrorBoundary>
      </main>
    </div>
  );
}
