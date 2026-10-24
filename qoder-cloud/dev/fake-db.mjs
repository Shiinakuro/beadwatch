// Credential-free in-memory database for local application tests.
export function createFakeDb() {
  const tables = new Map();
  const table = (name) => {
    if (!tables.has(name)) tables.set(name, []);
    return tables.get(name);
  };
  const project = (row, cols) => {
    if (!cols) return { ...row };
    const out = {};
    for (const col of cols.split(',').map((s) => s.trim())) out[col] = row[col];
    return out;
  };
  const result = (data) => ({ data, error: null });

  class Query {
    constructor(name) {
      this.name = name;
      this.filters = [];
      this.limitValue = Infinity;
      this.orderSpec = null;
    }
    select() { return this; }
    eq(col, value) { this.filters.push([col, value]); return this; }
    limit(n) { this.limitValue = n; return this; }
    order(col, options = {}) { this.orderSpec = { col, ascending: options.ascending !== false }; return this; }
    run() {
      let rows = table(this.name).filter((row) => this.filters.every(([k, v]) => row[k] === v));
      if (this.orderSpec) {
        const { col, ascending } = this.orderSpec;
        rows = [...rows].sort((a, b) => (a[col] < b[col] ? -1 : a[col] > b[col] ? 1 : 0) * (ascending ? 1 : -1));
      }
      rows = rows.slice(0, this.limitValue);
      return result(rows.map((row) => ({ ...row })));
    }
    then(resolve, reject) {
      try { resolve(this.run()); } catch (error) { reject(error); }
    }
  }

  class Mutation {
    constructor(name, action, row, values) {
      this.name = name;
      this.action = action;
      this.row = row;
      this.values = values;
      this.filters = [];
    }
    eq(col, value) { this.filters.push([col, value]); return this; }
    select() {
      if (this.action === 'insert') {
        const conflict = (this.row._onConflict ?? '').split(',').filter(Boolean);
        const collision = conflict.length > 0
          && table(this.name).some((existing) => conflict.every((key) => existing[key] === this.row[key]));
        if (collision) return result([]);
        const stored = { ...this.row };
        delete stored._onConflict;
        table(this.name).push(stored);
        return result([{ ...stored }]);
      }
      if (this.action === 'update') {
        const changed = [];
        for (const row of table(this.name)) {
          if (!this.filters.every(([key, value]) => row[key] === value)) continue;
          Object.assign(row, this.values);
          changed.push({ ...row });
        }
        return result(changed);
      }
      throw new Error('unknown mutation');
    }
  }

  return {
    from(name) {
      return {
        select() { return new Query(name); },
        insertIfAbsent(row, options = {}) { return new Mutation(name, 'insert', { ...row, _onConflict: options.onConflict }, null); },
        update(values) { return new Mutation(name, 'update', null, values); },
      };
    },
    tables,
  };
}
