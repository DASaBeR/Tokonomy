# login-demo

A tiny standard-library Python project used as a Taskrail benchmark fixture.

Run the tests with:

```sh
python -m unittest discover -s tests -t .
```

Code lives in `app/`. Keep modules small, use dependency injection for anything stateful or time-based, and avoid module-level mutable state.
