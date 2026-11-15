export type Role = 'Admin' | 'Staff' | 'Student';
export type Status = 'Active' | 'Paused' | 'Graduated';
export interface User { id: string; fullName: string; email: string; role: Role; departmentId: string | null; studentId: string | null; enabled: boolean; lockedUntil: string | null; lastLoginAt: string | null }
export interface Department { id: string; code: string; name: string }
export interface Student {
  id: string; studentCode: string; fullName: string; email: string; phone: string; nationalId: string;
  dateOfBirth: string; gender: 'Male' | 'Female' | 'Other'; address: string; departmentId: string;
  className: string; enrollmentYear: number; status: Status; gpa: number | null;
  createdAt: string; updatedAt: string; isMasked: boolean;
}
export type StudentInput = Partial<Omit<Student, 'id' | 'createdAt' | 'updatedAt' | 'isMasked'>>;
export interface DocumentRecord { id: string; studentId: string; originalName: string; contentType: string; size: number; uploadedAt: string; uploadedBy: string }
export interface Audit { id: string; userId: string; userName: string; action: string; target: string; result: string; ipAddress: string; createdAt: string; traceId: string }
export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number }
export interface Dashboard { totalStudents: number; activeStudents: number; totalDepartments: number; totalDocuments: number; byDepartment: { departmentId: string; name: string; count: number }[]; byStatus: { status: Status; count: number }[]; recentStudents: Student[]; recentActivity: Audit[] }
export interface SecurityInfo { checks: { id: string; title: string; status: string; description: string }[]; storage: string; authentication: string }
export interface OperationsInfo {
  startedAt: string; uptimeSeconds: number; totalRequests: number; failedRequests: number; deniedRequests: number;
  rateLimitedRequests: number; averageDurationMs: number; workingSetMb: number; cpuPercent?: number; databaseStatus: string; maintenance: boolean;
  changeStream: { enabled: boolean; status: string; lastEventAt: string | null; lastError: string | null };
  backup: { enabled: boolean; intervalHours: number; retentionCount: number; status: string; lastRunAt: string | null; nextRunAt: string | null };
  cache: { hits: number; misses: number };
}
export interface SecurityAlert { id: string; type: string; severity: string; message: string; count: number; createdAt: string; lastSeenAt: string; acknowledged: boolean }
export interface DatabaseEvent { id: string; collection: string; operation: string; documentId: string | null; createdAt: string; actorSource: string }
export interface BackupRun { id: string; status: string; trigger: string; startedAt: string; finishedAt: string | null; sizeBytes: number | null; errorCode: string | null }
