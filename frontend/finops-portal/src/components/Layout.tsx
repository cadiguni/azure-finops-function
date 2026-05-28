import { NavLink, Outlet } from 'react-router-dom';
import { LayoutDashboard, FileText, Lightbulb, AlertTriangle, Users } from 'lucide-react';
import UserProfile from './UserProfile';

const navItems = [
  { to: '/', icon: LayoutDashboard, label: 'Dashboard' },
  { to: '/reports', icon: FileText, label: 'Relatórios' },
  { to: '/recommendations', icon: Lightbulb, label: 'Recomendações' },
  { to: '/anomalies', icon: AlertTriangle, label: 'Anomalias' },
  { to: '/ownership', icon: Users, label: 'Ownership' },
];

export default function Layout() {
  return (
    <div className="layout">
      <aside className="sidebar">
        <div className="sidebar-header">
          <h1>FinOps</h1>
          <span className="sidebar-subtitle">Cost Platform</span>
        </div>
        <nav className="sidebar-nav">
          {navItems.map(({ to, icon: Icon, label }) => (
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`}
              end={to === '/'}
            >
              <Icon size={18} />
              <span>{label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer">
          <UserProfile />
        </div>
      </aside>
      <main className="main-content">
        <Outlet />
      </main>
    </div>
  );
}
