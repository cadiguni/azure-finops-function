import { Routes, Route, Navigate } from 'react-router-dom';
import { useAccess } from './auth/AccessContext';
import Layout from './components/Layout';
import ProtectedLayout from './components/ProtectedLayout';
import Dashboard from './pages/Dashboard';
import Recommendations from './pages/Recommendations';
import Anomalies from './pages/Anomalies';
import Databases from './pages/Databases';
import Ownership from './pages/Ownership';
import Resources from './pages/Resources';
import Allocation from './pages/Allocation';
import DataHealth from './pages/DataHealth';
import Budgets from './pages/Budgets';
import Migrations from './pages/Migrations';

function AdminMigrationRoute() {
  const { isAdmin } = useAccess();
  // Do not mount the page (or any of its fetching hooks) for readers, including direct URLs.
  return isAdmin ? <Migrations /> : <Navigate to="/" replace />;
}

function AdminDataHealthRoute() {
  const { isAdmin } = useAccess();
  // The overview fans out to every subscription; the backend also restricts it to FinOps.Admin.
  return isAdmin ? <DataHealth /> : <Navigate to="/" replace />;
}

function App() {
  return (
    <ProtectedLayout>
      <Routes>
        <Route element={<Layout />}>
          <Route path="/" element={<Dashboard />} />
          <Route path="/reports" element={<Navigate to="/recommendations" replace />} />
          <Route path="/recommendations" element={<Recommendations />} />
          <Route path="/resources" element={<Resources />} />
          <Route path="/allocation" element={<Allocation />} />
          <Route path="/anomalies" element={<Anomalies />} />
          <Route path="/databases" element={<Databases />} />
          <Route path="/ownership" element={<Ownership />} />
          <Route path="/data-health" element={<AdminDataHealthRoute />} />
          {/* Fora do menu até existir orçamento por time com alertas; mantido por URL direta. */}
          <Route path="/budgets" element={<Budgets />} />
          <Route path="/migrations" element={<AdminMigrationRoute />} />
        </Route>
      </Routes>
    </ProtectedLayout>
  );
}

export default App;
