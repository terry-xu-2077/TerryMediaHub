import { gbkTool } from 'file:///C:/OS/Servers/TerryMediaHub/src/TerryMediaHub.LocalService/JavaScript/assets/gbk.js';

export function __jsEvalReturn() {
  return {
    init() {},
    home() {
      return JSON.stringify({ class: [{ type_id: 'module', type_name: typeof gbkTool === 'function' ? 'ok' : 'bad' }] });
    }
  };
}
