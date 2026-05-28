import { Routes, Route } from 'react-router-dom';
import Layout from './components/Layout';
import ProtectedLayout from './components/ProtectedLayout';
import Dashboard from './pages/Dashboard';
import Reports from './pages/Reports';
import Recommendations from './pages/Recommendations';
import Anomalies from './pages/Anomalies';
import Ownership from './pages/Ownership';

function App() {
  return (
    <ProtectedLayout>
      <Routes>
        <Route element={<Layout />}>
          <Route path="/" element={<Dashboard />} />
          <Route path="/reports" element={<Reports />} />
          <Route path="/recommendations" element={<Recommendations />} />
          <Route path="/anomalies" element={<Anomalies />} />
          <Route path="/ownership" element={<Ownership />} />
        </Route>
      </Routes>
    </ProtectedLayout>
  );
}

export default App;
