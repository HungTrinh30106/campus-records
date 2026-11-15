import { describe, expect, it } from 'vitest';
import { buildQuery, initials, formatDate } from './lib';

describe('query filters', () => {
  it('preserves literal injection-like search text as one encoded parameter', () => {
    expect(buildQuery({ search: "x&role=Admin", page: 2, status: '' })).toBe('?search=x%26role%3DAdmin&page=2');
  });
  it('does not remove a numeric zero filter', () => {
    expect(buildQuery({ year: 0, departmentId: undefined })).toBe('?year=0');
  });
});
describe('record presentation', () => {
  it('uses the final two Vietnamese name parts for the avatar', () => expect(initials('Nguyễn Thị Minh Anh')).toBe('MA'));
  it('does not display invalid dates as NaN', () => expect(formatDate('not-a-date')).toBe('—'));
});
