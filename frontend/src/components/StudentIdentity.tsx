import { Link } from 'react-router-dom';
import { initials } from '../lib';

export default function StudentIdentity({ id, fullName, email, tone = 'teal' }: { id: string; fullName: string; email: string; tone?: string }) {
  // Student-provided strings remain React text nodes in every list using this cell.
  return <Link className="person-cell" to={`/students/${id}`}><span className={`avatar avatar-${tone}`}>{initials(fullName)}</span><div><strong>{fullName}</strong><small>{email || 'Chưa có email'}</small></div></Link>;
}
