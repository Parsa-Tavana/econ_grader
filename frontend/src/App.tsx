import { Routes, Route, Navigate } from "react-router-dom";
import AppLayout from "./layouts/AppLayout";
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

export default function App() {
  return (
    <Routes>
      <Route element={<AppLayout />}>
        <Route index element={<DashboardPage />} />
        <Route path="exams" element={<ExamsPage />} />
        <Route path="exams/:examId" element={<ExamDetailPage />} />
        <Route path="students" element={<StudentsPage />} />
        <Route path="students/:studentId" element={<StudentDetailPage />} />
        <Route path="grading/queue" element={<QueuePage />} />
        <Route path="grading/workspace/:answerId" element={<WorkspacePage />} />
        <Route path="evaluation" element={<EvaluationPage />} />
        <Route path="audit" element={<AuditPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
