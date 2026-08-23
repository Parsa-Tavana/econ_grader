import { api } from "./client";
import type { StudentDto, CreateStudentRequest } from "../types/models";

export async function listStudents(): Promise<StudentDto[]> {
  const { data } = await api.get<StudentDto[]>("/students");
  return data;
}
export async function getStudent(id: string): Promise<StudentDto> {
  const { data } = await api.get<StudentDto>(`/students/${id}`);
  return data;
}
export async function createStudent(req: CreateStudentRequest): Promise<StudentDto> {
  const { data } = await api.post<StudentDto>("/students", req);
  return data;
}