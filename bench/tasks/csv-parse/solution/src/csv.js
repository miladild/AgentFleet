'use strict';

function parseCsv(text) {
  if (text === '') return [];

  const rows = [];
  let row = [];
  let field = '';
  let inQuotes = false;
  let i = 0;

  while (i < text.length) {
    const char = text[i];

    if (inQuotes) {
      if (char === '"') {
        if (i + 1 < text.length && text[i + 1] === '"') {
          // Doubled quote becomes single quote
          field += '"';
          i += 2;
        } else {
          // End of quoted field
          inQuotes = false;
          i++;
          // Check what comes next
          if (i < text.length) {
            const nextChar = text[i];
            if (nextChar === ',') {
              row.push(field);
              field = '';
              i++;
            } else if (nextChar === '\n') {
              row.push(field);
              field = '';
              rows.push(row);
              row = [];
              i++;
            } else if (nextChar === '\r') {
              row.push(field);
              field = '';
              if (i + 1 < text.length && text[i + 1] === '\n') {
                i += 2;
              } else {
                i++;
              }
              rows.push(row);
              row = [];
            } else {
              throw new SyntaxError('Unexpected character after closing quote');
            }
          } else {
            // End of text after closing quote - push field and row
            row.push(field);
            field = '';
            rows.push(row);
            row = [];
          }
        }
      } else {
        field += char;
        i++;
      }
    } else {
      if (char === '"') {
        // Start of quoted field - only at beginning of field
        if (field === '') {
          inQuotes = true;
          i++;
        } else {
          // Quote in middle of unquoted field
          field += char;
          i++;
        }
      } else if (char === ',') {
        row.push(field);
        field = '';
        i++;
      } else if (char === '\n') {
        row.push(field);
        field = '';
        rows.push(row);
        row = [];
        i++;
      } else if (char === '\r') {
        row.push(field);
        field = '';
        if (i + 1 < text.length && text[i + 1] === '\n') {
          i += 2;
        } else {
          i++;
        }
        rows.push(row);
        row = [];
      } else {
        field += char;
        i++;
      }
    }
  }

  if (inQuotes) {
    throw new SyntaxError('Unterminated quote at end of input');
  }

  // Add final field and row only if we have actual content and haven't already pushed the row
  if (field !== '' || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows;
}

function toCsv(rows) {
  if (rows.length === 0) return '';

  return rows.map(row => {
    return row.map(field => {
      // Check if field needs quoting
      if (field.includes(',') || field.includes('"') || field.includes('\n') || field.includes('\r')) {
        // Quote the field and escape inner quotes by doubling
        return '"' + field.replace(/"/g, '""') + '"';
      }
      return field;
    }).join(',');
  }).join('\n');
}

module.exports = { parseCsv, toCsv };
