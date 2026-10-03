'use strict';

function slugify(text, options = {}) {
  // Non-string input returns empty string
  if (typeof text !== 'string') {
    return '';
  }

  // Lowercase
  let slug = text.toLowerCase();

  // Normalize Unicode and remove diacritics (accented characters)
  slug = slug.normalize('NFD').replace(/[̀-ͯ]/g, '');

  // Replace & with " and "
  slug = slug.replace(/&/g, ' and ');

  // Replace runs of non-alphanumeric characters with single hyphen
  slug = slug.replace(/[^a-z0-9]+/g, '-');

  // Remove leading and trailing hyphens
  slug = slug.replace(/^-+|-+$/g, '');

  // Apply maxLength if specified
  if (options.maxLength && options.maxLength > 0) {
    slug = slug.substring(0, options.maxLength);
    // Remove trailing hyphen if the cut left one
    slug = slug.replace(/-+$/, '');
  }

  return slug;
}

module.exports = { slugify };
