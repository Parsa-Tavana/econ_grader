import { Routes, Route, Navigate, Outlet, useLocation } from "react-router-dom";
import AppLayout from "./layouts/AppLayout";
import LoginPage from "./pages/LoginPage";
import DashboardPage from "./pages/DashboardPage";
import ExamsPage from "./pages/ExamsPage";
import ExamDetailPage from "./pages/ExamDetailPage";
import StudentsPage from "./pages/StudentsPage";
import StudentDetailPage from "./pages/StudentDetailPage";
import QueuePage from "./pages/QueuePage";
import WorkspacePage from "./pages/WorkspacePage";
import EvaluationPage from "./pages/EvaluationPage";
import AuditPage from "./pages/AuditPage";
import SettingsPage from "./pages/SettingsPage";
import UsersPage from "./pages/UsersPage";
import { getAuthUser, isLoggedIn } from "./api/auth";
import { isAdmin } from "./utils/roles";

/** Blocks unauthenticated access; remembers where the user was headed. */
function RequireAuth() {
  const location = useLocation();
  if (!isLoggedIn()) {
    const here = encodeURIComponent(location.pathname + location.search);
    return <Navigate to={`/login?next=${here}`} replace />;
  }
  return <Outlet />;
}

/** Admin-only subtree (audit log, user management). Others bounce to the dashboard. */
function RequireAdmin() {
  const user = getAuthUser();
  if (!isAdmin(user)) return <Navigate to="/" replace />;
  return <Outlet />;
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<RequireAuth />}>
        <Route element={<AppLayout />}>
          <Route index element={<DashboardPage />} />
          <Route path="exams" element={<ExamsPage />} />
          <Route path="exams/:examId" element={<ExamDetailPage />} />
          <Route path="students" element={<StudentsPage />} />
          <Route path="students/:studentId" element={<StudentDetailPage />} />
          <Route path="grading/queue" element={<QueuePage />} />
          <Route path="grading/workspace/:answerId" element={<WorkspacePage />} />
          <Route path="evaluation" element={<EvaluationPage />} />
          <Route element={<RequireAdmin />}>
            <Route path="audit" element={<AuditPage />} />
            <Route path="users" element={<UsersPage />} />
          </Route>
          <Route path="settings" element={<SettingsPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
