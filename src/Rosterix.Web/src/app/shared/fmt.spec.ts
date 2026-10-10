import { addDays, dateFmt, isoDate, money, monthLabel } from './fmt';

describe('formatting', () => {
  it('formats rupees with Indian grouping and no decimals', () => { expect(money(1234567)).toBe('₹12,34,567'); expect(money(525)).toBe('₹525'); expect(money(null)).toBe('—'); });
  it('formats dates without timezone drift', () => { expect(dateFmt('2026-08-01')).toBe('1 Aug 2026'); expect(dateFmt('2026-08-01', false)).toBe('1 Aug'); expect(dateFmt(undefined)).toBe('—'); });
  it('labels months in words', () => expect(monthLabel('2026-02')).toBe('February 2026'));
  it('adds days across month ends', () => { expect(addDays('2026-01-31', 1)).toBe('2026-02-01'); expect(isoDate(new Date(2026, 11, 5))).toBe('2026-12-05'); });
});
