'use strict';

// Resolve the options used by a paginated item request.
function requestOptions(options = {}) {
  return {
    page: options.page || 1,
    pageSize: options.pageSize || 25,
    sort: options.sort || 'name',
  };
}

module.exports = { requestOptions };
