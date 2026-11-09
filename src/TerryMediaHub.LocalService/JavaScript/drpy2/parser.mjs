import cheerio from './cheerio.min.js';

function selection(html, expression) {
  const $ = cheerio.load(String(html ?? ''));
  const selector = String(expression ?? '').trim();
  return { $, nodes: selector ? $(selector) : $.root() };
}

export function pdfa(html, expression) {
  const { $, nodes } = selection(html, expression);
  return nodes.toArray().map(node => $.html(node));
}

export function pdfh(html, expression) {
  const parts = String(expression ?? '').split('&&').map(x => x.trim()).filter(Boolean);
  if (!parts.length) return '';
  const attribute = parts.pop();
  const selector = parts.join(' ');
  const { nodes } = selection(html, selector);
  const first = nodes.first();
  if (!first.length) return '';
  if (/^text$/i.test(attribute)) return first.text().trim();
  if (/^html$/i.test(attribute)) return first.html() ?? '';
  return first.attr(attribute) ?? '';
}

export function pd(html, expression, baseUrl) {
  const value = pdfh(html, expression);
  if (!value) return '';
  try { return new URL(value, baseUrl).href; } catch { return value; }
}
